using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.ResourceLocations;
using System.IO;
using UnityEditor;
#if UNITY_EDITOR
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
#endif
public class AddressableSystem : SystemBase
{
    private CancellationTokenSource cts;
    bool progressisDone = false;
    bool isProgress = true;
    public async override void Initialize()
    {
        cts = new CancellationTokenSource();
        CancellationToken cancellationToken = cts.Token;
#if UNITY_EDITOR
        //如果在Editor中PlayMode不是使用Existing build就直接跳掉
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var fastModeIndex = settings.DataBuilders.FindIndex(x => x is BuildScriptFastMode);
        var virtualModeIndex = settings.DataBuilders.FindIndex(x => x is BuildScriptVirtualMode);
        if (settings.ActivePlayModeDataBuilderIndex == fastModeIndex || settings.ActivePlayModeDataBuilderIndex == virtualModeIndex)
        {
            Debug.LogError("FastMode");
            cts.Cancel();
            progressisDone = true;
            return;
        }
#endif
        //判斷本地端是否第一次運行，Y => 清空緩存準備進行讀取及下載動作
        if (GetDependencyCheck() == 0)
            Addressables.ClearResourceLocators();

        AsyncOperationHandle<IResourceLocator> init = Addressables.InitializeAsync();
        await init.Task;
        //開始流程控制
        Task progress = Progressing(cancellationToken);
        //確認連線狀態
        Task<bool> isConnect = Connecting(cancellationToken);
        await isConnect;


        //如果是第一次啟動讀取內部資源並生成相依性資源
        if (GetDependencyCheck() == 0)
        {
            Task loadDefault = LoadDefaultContent();
            await loadDefault;
        }
        else
        {
            if (isConnect.Result)
            {
                Debug.Log("成功連接到 RemoteLoadPath");
                List<string> catalogs = await CheckCatalogs();
                bool isUpdateCata = await UpdateCatalogs(catalogs, cancellationToken);

                if (isUpdateCata)
                {
                    Task download = DownloadAssets(cancellationToken);
                    await download;
                }
            }
            else
            {
                Debug.LogError("連接到 RemoteLoadPath 失敗");
                cts.Cancel();
            }
        }




    }


    #region PlayerPrefs
    private int GetDependencyCheck()
    {
        if (PlayerPrefs.HasKey("dependenciesDownload"))
        {

            return PlayerPrefs.GetInt("dependenciesDownload");
        }
        else
        {
            PlayerPrefs.SetInt("dependenciesDownload", 0);
            return PlayerPrefs.GetInt("dependenciesDownload");
        }
    }
    private void SetDependencyCheck()
    {
        PlayerPrefs.SetInt("dependenciesDownload", 1);
    }
    #endregion

    /// <summary>
    /// 整個流程的Task控制 
    /// </summary>
    /// <param name="cancellationToken"> Task控制取消的Token </param>
    /// <returns></returns>
    public async Task Progressing(CancellationToken cancellationToken)
    {
        while (isProgress)
        {
            if (cancellationToken.IsCancellationRequested) //流程中監聽Cancel事件
            {
                cancellationToken.ThrowIfCancellationRequested();
                progressisDone = true;
                isProgress = false;
                Debug.LogError("任務取消");
            }
            await Task.Delay(100);
        }
        progressisDone = true;
    }
    /// <summary>
    /// 完成AA流程的Callback
    /// </summary>
    /// <returns></returns>
    public async Task<bool> CompleteCallback()
    {
        while (!progressisDone)
        {
            await Task.Delay(1000);
        }
        cts.Cancel();
        return progressisDone;
    }
    public async Task<bool> Connecting(CancellationToken cancellationToken)
    {

        UnityWebRequest webRequest = UnityWebRequest.Head("ftp://192.168.1.35:21");
        webRequest.SendWebRequest();
        while (!webRequest.isDone)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
            {
                Debug.LogError("取消連線");
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        if (webRequest.result == UnityWebRequest.Result.Success)
        {
            return true;
        }
        else
        {
            webRequest.Dispose();
            cts.Cancel();
            return false;
        }
    }

    /// <summary>
    /// 更換讀取資源位置，會繞過catalog直接讀取指定路徑
    /// </summary>
    /// <param name="location"></param>
    /// <returns></returns>
    string InternalIdTransformFunc(IResourceLocation location)
    {
        string tmp = location.Dependencies[0].InternalId;
        tmp = tmp.Replace("ftp://192.168.1.35:21", MySpace.AddressableWrapper.defaultPath);
        return tmp;
    }
    private async Task LoadDefaultContent()
    {
        //Addressables.InternalIdTransformFunc = InternalIdTransformFunc;
        //讀取本地資源Catalog
        AsyncOperationHandle<IResourceLocator> handle = Addressables.LoadContentCatalogAsync(MySpace.AddressableWrapper.URL, false);
        await handle.Task;

        List<Task> allDDtask = new List<Task>();
        if (handle.Status == AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"讀取本地Catalog 載入初始資源中...");
            foreach (var loc in Addressables.ResourceLocators)
            {
                foreach (var key in loc.Keys)
                {
                    AsyncOperationHandle DDhandle = Addressables.DownloadDependenciesAsync(key);
                    DDhandle.Completed += ((t) =>
                    {
                        if (t.Status == AsyncOperationStatus.Succeeded)
                        {
                        }
                        else
                        {
                            Debug.LogError("載入初始資源失敗");
                            cts.Cancel();
                            progressisDone = true;
                        }
                    });
                    allDDtask.Add(DDhandle.Task);
                }
            }
        }
        else
        {
            Debug.LogError("載入初始資源失敗");
        }
        await Task.WhenAll(allDDtask);
        Addressables.Release(handle);
        progressisDone = true;
        Debug.Log("次數+1");
        SetDependencyCheck();

    }


    private async Task<List<string>> CheckCatalogs()
    {
        Debug.LogError("檢查 catalogs 更新...");
        List<string> catalogsToUpdate = new List<string>();
        AsyncOperationHandle<List<string>> checkForUpdateHandle = Addressables.CheckForCatalogUpdates(false);
        await checkForUpdateHandle.Task;
        if (checkForUpdateHandle.Status == AsyncOperationStatus.Succeeded)
        {
            catalogsToUpdate.AddRange(checkForUpdateHandle.Result);
        }
        Addressables.Release(checkForUpdateHandle);
        return catalogsToUpdate;
    }

    private async Task<bool> UpdateCatalogs(List<string> logs, CancellationToken cancellationToken)
    {
        if (logs.Count > 0)
        {
            Debug.LogError("下載新的目錄");
            AsyncOperationHandle<List<IResourceLocator>> updateHandle = Addressables.UpdateCatalogs(logs, false);
            await updateHandle.Task;
            if (updateHandle.Status != AsyncOperationStatus.Succeeded)
            {
                return false;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                Addressables.Release(updateHandle);
                Debug.LogError("更新目錄失敗");
                cancellationToken.ThrowIfCancellationRequested();
            }
            Addressables.Release(updateHandle);
        }
        else
        {
            Debug.LogError("無新目錄");
        }
        return true;
    }


    async Task DownloadAssets(CancellationToken cancellationToken)
    {
        Debug.LogError("檢查 資源 更新...");
        List<Task> downloadAssets = new List<Task>();
        foreach (var loc in Addressables.ResourceLocators)
        {

            foreach (var key in loc.Keys)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Debug.LogError("取消下載");
                    cancellationToken.ThrowIfCancellationRequested();
                }

                AsyncOperationHandle<long> getDownloadSize = Addressables.GetDownloadSizeAsync(key);   //透過http 可行 透過ftp 不行           
                if (getDownloadSize.Status == AsyncOperationStatus.Succeeded)
                {
                    long tmp = getDownloadSize.Result;

                    if (tmp != null) //先改成可通過的樣子 原本是 tmp > 0
                    {
                        try
                        {
                            AsyncOperationHandle downloadDependencies = Addressables.DownloadDependenciesAsync(key);
                            downloadAssets.Add(downloadDependencies.Task);
                            downloadDependencies.Completed += (handle) =>
                            {
                                if (handle.Status == AsyncOperationStatus.Succeeded)
                                {
                                }
                                else
                                {
                                    cts.Cancel();
                                }
                                Addressables.Release(downloadDependencies);
                            };
                            Debug.LogError($"下載更新資源...");
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        //Debug.LogError($"無更新資源");
                    }
                    Addressables.Release(getDownloadSize);
                }
                else
                {
                    cts.Cancel();
                    Debug.LogError("檢查失敗，請確認網路狀態");
                }

            }

        }
        await Task.WhenAll(downloadAssets);
        Debug.LogError("Done !");
        progressisDone = true;
        cts.Cancel();
    }

    #region 測試用
    //private void CheckRemoteLoadPath()
    //{
    //    AsyncOperationHandle<IResourceLocator> init = Addressables.InitializeAsync();

    //    AsyncOperationHandle getDownloadSize = Addressables.GetDownloadSizeAsync("MainPage_Start");
    //    getDownloadSize.Completed += OnCatalogLoaded;
    //}

    //private void OnCatalogLoaded(AsyncOperationHandle handle)
    //{
    //    if (handle.Status == AsyncOperationStatus.Succeeded)
    //    {
    //        Debug.LogError("成功連接到 RemoteLoadPath" + $"Result : {handle.Result}");

    //    }
    //    else
    //    {
    //        Debug.LogError("連接到 RemoteLoadPath 失敗：" + handle.OperationException);
    //    }
    //}
    #endregion



}
