#pragma once

#include "BlobAssetRequest.h"

#include <CesiumAsync/Future.h>
#include <CesiumAsync/IAssetAccessor.h>
#include <CesiumAsync/IAssetRequest.h>

#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace CesiumForUnityNative {

// ============================================================
//  Global Blob pointers (set from C# via P/Invoke, Part 8)
//  Declared extern here; defined in BlobAssetAccessor.cpp
// ============================================================
extern const std::byte* g_terrainBlobPtr;
extern int g_terrainBlobSize;
extern const std::byte* g_imageryBlobPtrs[4];
extern int g_imageryBlobSizes[4];

// ============================================================
//  BlobAssetAccessor
//
//  Intercepts cesium-native IAssetAccessor::get() calls and
//  serves tile data directly from in-memory Blob buffers.
//
//  URL patterns handled:
//    */layer.json              -> terrain metadata (JSON)
//    */{z}/{x}/{y}.terrain     -> quantized-mesh tile
//    */tilemapresource.xml     -> imagery metadata (XML)
//    */{z}/{x}/{y}.png         -> imagery tile (PNG)
//
//  Any other URL falls through to the HTTP fallback chain.
// ============================================================
class BlobAssetAccessor : public CesiumAsync::IAssetAccessor {
public:
  // pFallback: the existing HTTP accessor chain
  // (GunzipAssetAccessor wrapping CachingAssetAccessor)
  explicit BlobAssetAccessor(
      std::shared_ptr<CesiumAsync::IAssetAccessor> pFallback);

  virtual ~BlobAssetAccessor() = default;

  // Main interception point.
  // Matched URLs: resolved immediately from Blob memory.
  // Unmatched URLs: delegated to _pFallback.
  virtual CesiumAsync::Future<std::shared_ptr<CesiumAsync::IAssetRequest>>
  get(const CesiumAsync::AsyncSystem& asyncSystem,
      const std::string& url,
      const std::vector<THeader>& headers = {}) override;

  // POST/PATCH etc. — always delegated to fallback (Blob is read-only).
  virtual CesiumAsync::Future<std::shared_ptr<CesiumAsync::IAssetRequest>>
  request(
      const CesiumAsync::AsyncSystem& asyncSystem,
      const std::string& verb,
      const std::string& url,
      const std::vector<THeader>& headers = std::vector<THeader>(),
      const std::span<const std::byte>& contentPayload = {}) override;

  // No-op: Blob needs no ticking.
  virtual void tick() noexcept override;

private:
  std::shared_ptr<CesiumAsync::IAssetAccessor> _pFallback;

  // ---- URL pattern matching ----
  // Returns true and populates z/x/y if url matches */{z}/{x}/{y}.terrain
  static bool matchTerrainTile(const std::string& url, int& z, int& x, int& y);

  // Returns true and populates z/x/y if url matches */{z}/{x}/{y}.png
  static bool matchImageryTile(const std::string& url, int& z, int& x, int& y);

  // ---- Metadata generation ----
  // Returns cached layer.json bytes (generated once on first call).
  const std::vector<std::byte>& getLayerJson();

  // Returns cached tilemapresource.xml bytes (generated once on first call).
  const std::vector<std::byte>& getTileMapResourceXml();

  // ---- Blob index search ----
  // Searches the terrain Blob for tile (z, x, y).
  // Returns {ptr, size} on hit, {nullptr, 0} on miss.
  static std::pair<const std::byte*, std::size_t>
  tryGetFromTerrainBlob(int z, int x, int y);

  // Searches the correct imagery Blob for tile (z, x, y).
  // Returns {ptr, size} on hit, {nullptr, 0} on miss.
  static std::pair<const std::byte*, std::size_t>
  tryGetFromImageryBlob(int z, int x, int y);

  // Searches a single Blob buffer for tile (x, y) within the given zoom level.
  static std::pair<const std::byte*, std::size_t>
  searchBlob(const std::byte* blobPtr, int blobSize, int z, int x, int y);

  // ---- Cached metadata ----
  std::vector<std::byte> _layerJsonCache;
  std::vector<std::byte> _tileMapXmlCache;
};

} // namespace CesiumForUnityNative
