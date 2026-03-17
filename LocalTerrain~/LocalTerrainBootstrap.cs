using System;
using System.IO;
using System.Threading.Tasks;
using CesiumForUnity;
using LocalTerrain;
using UnityEngine;

/// <summary>
/// Local Terrain Pipeline bootstrap MonoBehaviour.
///
/// Attach to a GameObject in the scene.
/// Assign Tileset and RasterOverlay references in the Inspector.
///
/// Initialization order:
///   Step 1: Load all 5 Blob files in parallel           (~2-3 sec)
///   Step 2: Pin byte[] and export pointers to C++       (~1 ms)
///   Step 3: Configure Cesium3DTileset URL               (instant)
///   Step 4: Configure CesiumTileMapServiceRasterOverlay (instant)
///   Step 5: RecreateTileset()                           (triggers cesium-native)
/// </summary>
public class LocalTerrainBootstrap : MonoBehaviour
{
    // ----------------------------------------------------------------
    //  Inspector fields
    // ----------------------------------------------------------------
    [Header("Cesium Components")]
    [Tooltip("Cesium3DTileset component on this or a child GameObject.")]
    public Cesium3DTileset Tileset;

    [Tooltip("CesiumTileMapServiceRasterOverlay component.")]
    public CesiumTileMapServiceRasterOverlay RasterOverlay;

    [Header("Local Terrain Settings")]
    [Tooltip("Base URL prefix used for BlobAssetAccessor interception. Do not change unless you modify the C++ pattern matching.")]
    public string LocalBlobBaseUrl = "http://local-blob";

    [Tooltip("Terrain data directory name inside StreamingAssets.")]
    public string TerrainDataDirName = "TerrainData";

    [Header("Raster Overlay Zoom")]
    public int MinimumZoomLevel = 6;
    public int MaximumZoomLevel = 10;

    // ----------------------------------------------------------------
    //  State
    // ----------------------------------------------------------------
    private bool _initialized = false;

    // ----------------------------------------------------------------
    //  Unity lifecycle
    // ----------------------------------------------------------------
    private async void Start()
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalTerrainBootstrap] Initialization failed: {e}");
        }
    }

    private void OnApplicationQuit()
    {
        BlobMemoryBridge.Release();
        Debug.Log("[LocalTerrainBootstrap] BlobMemoryBridge released on quit.");
    }

    // ----------------------------------------------------------------
    //  Main initialization sequence
    // ----------------------------------------------------------------
    private async Task InitializeAsync()
    {
        if (_initialized)
        {
            Debug.LogWarning("[LocalTerrainBootstrap] Already initialized.");
            return;
        }

        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // ---- Validate Inspector references ----
        if (Tileset == null)
            throw new InvalidOperationException(
                "[LocalTerrainBootstrap] Tileset is not assigned in the Inspector.");
        if (RasterOverlay == null)
            throw new InvalidOperationException(
                "[LocalTerrainBootstrap] RasterOverlay is not assigned in the Inspector.");

        // ============================================================
        //  Step 1: Load all 5 Blob files in parallel
        // ============================================================
        string terrainDataDir = Path.Combine(
            Application.streamingAssetsPath, TerrainDataDirName);

        Debug.Log($"[LocalTerrainBootstrap] Step 1: Loading Blob files from {terrainDataDir}");
        await BlobLoader.LoadAllAsync(terrainDataDir);
        Debug.Log("[LocalTerrainBootstrap] Step 1 complete.");

        // ============================================================
        //  Step 2: Pin byte[] and export native pointers to C++
        // ============================================================
        Debug.Log("[LocalTerrainBootstrap] Step 2: Pinning memory and exporting to C++...");
        BlobMemoryBridge.PinAndExport();
        Debug.Log("[LocalTerrainBootstrap] Step 2 complete.");

        // ============================================================
        //  Step 3: Configure Cesium3DTileset
        //  URL pattern: "http://local-blob/layer.json"
        //  BlobAssetAccessor intercepts any URL containing "layer.json"
        // ============================================================
        Debug.Log("[LocalTerrainBootstrap] Step 3: Configuring Cesium3DTileset...");

        string layerJsonUrl = $"{LocalBlobBaseUrl}/layer.json";
        Tileset.tilesetSource = CesiumDataSource.FromUrl;
        Tileset.url = layerJsonUrl;

        Debug.Log($"[LocalTerrainBootstrap] Step 3 complete. url={layerJsonUrl}");

        // ============================================================
        //  Step 4: Configure CesiumTileMapServiceRasterOverlay
        //  URL pattern: "http://local-blob/tilemapresource.xml"
        //  BlobAssetAccessor intercepts any URL containing "tilemapresource.xml"
        // ============================================================
        Debug.Log("[LocalTerrainBootstrap] Step 4: Configuring RasterOverlay...");

        string tmsUrl = $"{LocalBlobBaseUrl}/tilemapresource.xml";
        RasterOverlay.url = tmsUrl;
        RasterOverlay.specifyZoomLevels = true;
        RasterOverlay.minimumLevel = MinimumZoomLevel;
        RasterOverlay.maximumLevel = MaximumZoomLevel;

        Debug.Log($"[LocalTerrainBootstrap] Step 4 complete. url={tmsUrl} " +
                  $"zoom={MinimumZoomLevel}~{MaximumZoomLevel}");

        // ============================================================
        //  Step 5: Trigger cesium-native tileset creation
        //  RecreateTileset() causes cesium-native to call:
        //    IAssetAccessor::get("http://local-blob/layer.json")
        //  which BlobAssetAccessor intercepts immediately.
        // ============================================================
        Debug.Log("[LocalTerrainBootstrap] Step 5: RecreateTileset()...");
        Tileset.RecreateTileset();
        Debug.Log("[LocalTerrainBootstrap] Step 5 complete.");

        // ============================================================
        //  Done
        // ============================================================
        totalSw.Stop();
        _initialized = true;

        Debug.Log(
            $"[LocalTerrainBootstrap] ===== Initialization complete in " +
            $"{totalSw.ElapsedMilliseconds} ms =====\n" +
            $"  layer.json URL  : {layerJsonUrl}\n" +
            $"  TMS URL         : {tmsUrl}\n" +
            $"  Zoom (terrain)  : 6 ~ 11\n" +
            $"  Zoom (imagery)  : {MinimumZoomLevel} ~ {MaximumZoomLevel}\n" +
            $"  Blob total size : " +
            $"{(BlobLoader.TerrainBlob.LongLength + BlobLoader.ImageryBlobs[0].LongLength + BlobLoader.ImageryBlobs[1].LongLength + BlobLoader.ImageryBlobs[2].LongLength + BlobLoader.ImageryBlobs[3].LongLength) / 1024 / 1024.0f:F1} MB in RAM");
    }
}