using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LocalTerrain
{
    /// <summary>
    /// Pins BlobLoader's byte[] arrays in memory and passes their native
    /// pointers to C++ via P/Invoke (SetTerrainBlobPtr / SetImageryBlobPtr).
    ///
    /// Call PinAndExport() after BlobLoader.LoadAllAsync() completes.
    /// Call Release() on application quit to free GCHandles.
    /// </summary>
    public static class BlobMemoryBridge
    {
        // ----------------------------------------------------------------
        //  P/Invoke declarations
        //  DLL name: "CesiumForUnityNative" (no extension needed on Windows)
        // ----------------------------------------------------------------
        [DllImport("CesiumForUnityNative", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetTerrainBlobPtr(IntPtr ptr, int size);

        [DllImport("CesiumForUnityNative", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetImageryBlobPtr(int index, IntPtr ptr, int size);

        // ----------------------------------------------------------------
        //  GCHandle storage (keeps byte[] pinned until Release())
        // ----------------------------------------------------------------
        private static GCHandle _terrainHandle;
        private static GCHandle[] _imageryHandles = new GCHandle[4];
        private static bool _isPinned = false;

        // ----------------------------------------------------------------
        //  PinAndExport: pin all byte[] and send pointers to C++
        // ----------------------------------------------------------------
        public static void PinAndExport()
        {
            if (_isPinned)
            {
                Debug.LogWarning("[BlobMemoryBridge] Already pinned. Skipping.");
                return;
            }

            if (!BlobLoader.IsLoaded)
                throw new InvalidOperationException(
                    "[BlobMemoryBridge] BlobLoader.LoadAllAsync() must complete before PinAndExport().");

            // ---- terrain ----
            _terrainHandle = GCHandle.Alloc(BlobLoader.TerrainBlob, GCHandleType.Pinned);
            IntPtr terrainPtr = _terrainHandle.AddrOfPinnedObject();
            SetTerrainBlobPtr(terrainPtr, BlobLoader.TerrainBlob.Length);

            Debug.Log($"[BlobMemoryBridge] TerrainBlob pinned at 0x{terrainPtr:X16}" +
                      $" ({BlobLoader.TerrainBlob.Length} bytes)");

            // Sanity check: first 8 bytes should be "TERRBLOB"
            ValidateMagic(BlobLoader.TerrainBlob, "TERRBLOB", "terrain.bin");

            // ---- imagery 0~3 ----
            for (int i = 0; i < 4; i++)
            {
                _imageryHandles[i] = GCHandle.Alloc(
                    BlobLoader.ImageryBlobs[i], GCHandleType.Pinned);
                IntPtr imgPtr = _imageryHandles[i].AddrOfPinnedObject();
                SetImageryBlobPtr(i, imgPtr, BlobLoader.ImageryBlobs[i].Length);

                Debug.Log($"[BlobMemoryBridge] ImageryBlob[{i}] pinned at 0x{imgPtr:X16}" +
                          $" ({BlobLoader.ImageryBlobs[i].Length} bytes)");

                // Sanity check: first 8 bytes should be "IMGEBLOB"
                ValidateMagic(BlobLoader.ImageryBlobs[i], "IMGEBLOB", $"imagery_00{i}.bin");
            }

            _isPinned = true;
            Debug.Log("[BlobMemoryBridge] All Blob pointers exported to C++.");
        }

        // ----------------------------------------------------------------
        //  Release: free all GCHandles (call on application quit)
        // ----------------------------------------------------------------
        public static void Release()
        {
            if (!_isPinned) return;

            if (_terrainHandle.IsAllocated)
                _terrainHandle.Free();

            for (int i = 0; i < 4; i++)
                if (_imageryHandles[i].IsAllocated)
                    _imageryHandles[i].Free();

            _isPinned = false;
            Debug.Log("[BlobMemoryBridge] All GCHandles released.");
        }

        // ----------------------------------------------------------------
        //  Magic byte validation (8-3 sanity check)
        // ----------------------------------------------------------------
        private static void ValidateMagic(byte[] blob, string expected, string fileName)
        {
            if (blob.Length < 8)
            {
                Debug.LogError($"[BlobMemoryBridge] {fileName}: too small to read magic.");
                return;
            }

            string magic = System.Text.Encoding.ASCII.GetString(blob, 0, 8);
            if (magic == expected)
                Debug.Log($"[BlobMemoryBridge] {fileName}: magic OK ({magic})");
            else
                Debug.LogError(
                    $"[BlobMemoryBridge] {fileName}: magic MISMATCH! " +
                    $"expected '{expected}', got '{magic}'");
        }
    }
}