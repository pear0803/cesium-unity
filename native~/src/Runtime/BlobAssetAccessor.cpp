#include "BlobAssetAccessor.h"

#include <CesiumAsync/AsyncSystem.h>

#include <spdlog/spdlog.h>

#include <cassert>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <string>

// ============================================================
//  extern "C" P/Invoke entry points (used by Part 8 C# bridge)
//  __declspec(dllexport): Windows DLL symbol export
// ============================================================
extern "C" {

__declspec(dllexport) void SetTerrainBlobPtr(const std::byte* ptr, int size) {
  CesiumForUnityNative::g_terrainBlobPtr = ptr;
  CesiumForUnityNative::g_terrainBlobSize = size;
  spdlog::info("[BlobAssetAccessor] TerrainBlob registered: {} bytes", size);
}

__declspec(dllexport) void
SetImageryBlobPtr(int index, const std::byte* ptr, int size) {
  if (index < 0 || index >= 4)
    return;
  CesiumForUnityNative::g_imageryBlobPtrs[index] = ptr;
  CesiumForUnityNative::g_imageryBlobSizes[index] = size;
  spdlog::info(
      "[BlobAssetAccessor] ImageryBlob[{}] registered: {} bytes",
      index,
      size);
}

} // extern "C"

namespace CesiumForUnityNative {

// ============================================================
//  Global Blob pointer definitions
// ============================================================
const std::byte* g_terrainBlobPtr = nullptr;
int g_terrainBlobSize = 0;
const std::byte* g_imageryBlobPtrs[4] = {nullptr, nullptr, nullptr, nullptr};
int g_imageryBlobSizes[4] = {0, 0, 0, 0};

// ============================================================
//  Blob binary layout helpers (little-endian)
// ============================================================
namespace {

template <typename T> T readLE(const std::byte* base, std::size_t offset) {
  T value{};
  std::memcpy(&value, base + offset, sizeof(T));
  return value;
}

struct GlobalHeader {
  uint64_t dirOffset;
  uint32_t zoomCount;
  uint32_t zoomStart;
  uint32_t zoomEnd;
};

GlobalHeader parseGlobalHeader(const std::byte* blob) {
  GlobalHeader h{};
  h.zoomStart = readLE<uint32_t>(blob, 12);
  h.zoomEnd = readLE<uint32_t>(blob, 16);
  h.zoomCount = readLE<uint32_t>(blob, 20);
  h.dirOffset = readLE<uint64_t>(blob, 24);
  return h;
}

struct ZoomDirEntry {
  uint32_t zoom;
  uint64_t tileCount;
  uint64_t indexOffset;
  uint64_t dataOffset;
};

ZoomDirEntry
parseZoomDirEntry(const std::byte* blob, uint64_t dirOffset, uint32_t i) {
  std::size_t base = static_cast<std::size_t>(dirOffset) + i * 28u;
  ZoomDirEntry e{};
  e.zoom = readLE<uint32_t>(blob, base);
  e.tileCount = readLE<uint64_t>(blob, base + 4);
  e.indexOffset = readLE<uint64_t>(blob, base + 12);
  e.dataOffset = readLE<uint64_t>(blob, base + 20);
  return e;
}

struct IndexEntry {
  uint32_t x;
  uint32_t y;
  uint64_t offset;
  uint32_t size;
};

IndexEntry parseIndexEntry(const std::byte* base) {
  IndexEntry e{};
  e.x = readLE<uint32_t>(base, 0);
  e.y = readLE<uint32_t>(base, 4);
  e.offset = readLE<uint64_t>(base, 8);
  e.size = readLE<uint32_t>(base, 16);
  return e;
}

const std::byte* binarySearchIndex(
    const std::byte* indexBase,
    uint64_t tileCount,
    uint32_t tx,
    uint32_t ty) {

  int64_t lo = 0;
  int64_t hi = static_cast<int64_t>(tileCount) - 1;

  while (lo <= hi) {
    int64_t mid = lo + (hi - lo) / 2;
    const std::byte* entry = indexBase + mid * 20;
    uint32_t ex = readLE<uint32_t>(entry, 0);
    uint32_t ey = readLE<uint32_t>(entry, 4);

    if (ex == tx && ey == ty)
      return entry;
    if (ex < tx || (ex == tx && ey < ty))
      lo = mid + 1;
    else
      hi = mid - 1;
  }
  return nullptr;
}

bool parseInt(const std::string& s, int& out) {
  try {
    std::size_t pos;
    out = std::stoi(s, &pos);
    return pos == s.size();
  } catch (...) {
    return false;
  }
}

std::string stripQuery(const std::string& url) {
  auto q = url.find('?');
  if (q != std::string::npos)
    return url.substr(0, q);
  auto f = url.find('#');
  if (f != std::string::npos)
    return url.substr(0, f);
  return url;
}

} // anonymous namespace

// ============================================================
//  searchBlob
// ============================================================
std::pair<const std::byte*, std::size_t> BlobAssetAccessor::searchBlob(
    const std::byte* blobPtr,
    int blobSize,
    int z,
    int x,
    int y) {

  if (!blobPtr || blobSize < 64)
    return {nullptr, 0};

  GlobalHeader gh = parseGlobalHeader(blobPtr);

  for (uint32_t i = 0; i < gh.zoomCount; ++i) {
    ZoomDirEntry zd = parseZoomDirEntry(blobPtr, gh.dirOffset, i);
    if (static_cast<int>(zd.zoom) != z)
      continue;

    const std::byte* indexBase =
        blobPtr + static_cast<std::size_t>(zd.indexOffset);

    const std::byte* entry = binarySearchIndex(
        indexBase,
        zd.tileCount,
        static_cast<uint32_t>(x),
        static_cast<uint32_t>(y));

    if (!entry)
      return {nullptr, 0};

    IndexEntry ie = parseIndexEntry(entry);
    const std::byte* tilePtr = blobPtr + static_cast<std::size_t>(ie.offset);
    return {tilePtr, static_cast<std::size_t>(ie.size)};
  }

  return {nullptr, 0};
}

// ============================================================
//  tryGetFromTerrainBlob / tryGetFromImageryBlob
// ============================================================
std::pair<const std::byte*, std::size_t>
BlobAssetAccessor::tryGetFromTerrainBlob(int z, int x, int y) {
  return searchBlob(g_terrainBlobPtr, g_terrainBlobSize, z, x, y);
}

std::pair<const std::byte*, std::size_t>
BlobAssetAccessor::tryGetFromImageryBlob(int z, int x, int y) {
  int idx = -1;
  if (z == 6 || z == 7)
    idx = 0;
  else if (z == 8)
    idx = 1;
  else if (z == 9)
    idx = 2;
  else if (z == 10)
    idx = 3;
  else
    return {nullptr, 0};

  return searchBlob(g_imageryBlobPtrs[idx], g_imageryBlobSizes[idx], z, x, y);
}

// ============================================================
//  URL pattern matching
// ============================================================
bool BlobAssetAccessor::matchTerrainTile(
    const std::string& rawUrl,
    int& z,
    int& x,
    int& y) {

  std::string url = stripQuery(rawUrl);

  const std::string ext = ".terrain";
  if (url.size() < ext.size())
    return false;
  if (url.substr(url.size() - ext.size()) != ext)
    return false;

  std::string path = url.substr(0, url.size() - ext.size());

  auto p3 = path.rfind('/');
  if (p3 == std::string::npos)
    return false;
  auto p2 = path.rfind('/', p3 - 1);
  if (p2 == std::string::npos)
    return false;
  auto p1 = path.rfind('/', p2 - 1);
  if (p1 == std::string::npos)
    return false;

  std::string sz = path.substr(p1 + 1, p2 - p1 - 1);
  std::string sx = path.substr(p2 + 1, p3 - p2 - 1);
  std::string sy = path.substr(p3 + 1);

  return parseInt(sz, z) && parseInt(sx, x) && parseInt(sy, y);
}

bool BlobAssetAccessor::matchImageryTile(
    const std::string& rawUrl,
    int& z,
    int& x,
    int& y) {

  std::string url = stripQuery(rawUrl);

  const std::string ext = ".png";
  if (url.size() < ext.size())
    return false;
  if (url.substr(url.size() - ext.size()) != ext)
    return false;

  std::string path = url.substr(0, url.size() - ext.size());

  auto p3 = path.rfind('/');
  if (p3 == std::string::npos)
    return false;
  auto p2 = path.rfind('/', p3 - 1);
  if (p2 == std::string::npos)
    return false;
  auto p1 = path.rfind('/', p2 - 1);
  if (p1 == std::string::npos)
    return false;

  std::string sz = path.substr(p1 + 1, p2 - p1 - 1);
  std::string sx = path.substr(p2 + 1, p3 - p2 - 1);
  std::string sy = path.substr(p3 + 1);

  return parseInt(sz, z) && parseInt(sx, x) && parseInt(sy, y);
}

// ============================================================
//  Metadata generation
// ============================================================
const std::vector<std::byte>& BlobAssetAccessor::getLayerJson() {
  if (!_layerJsonCache.empty())
    return _layerJsonCache;

  // ============================================================
  // [FIX] cesium-native available[] 인덱스 규칙:
  //   available[i] = zoom i 의 타일 목록 (절대값, minzoom 기준 offset 아님)
  //
  //   Blob에 zoom 0~11이 전부 존재하므로
  //   available도 zoom 0~11을 그대로 기���.
  //   zoom 0~5는 전세계 저해상도 타일이 존재.
  //   zoom 6~11은 한반도 커버리지 타일만 존재.
  //
  //   minzoom/maxzoom은 cesium-native LOD 제어용이므로 실제 범위로 설정.
  // ============================================================
  const std::string json = R"({
  "tilejson": "2.1.0",
  "format": "quantized-mesh-1.0",
  "version": "1.0.0",
  "scheme": "tms",
  "tiles": ["{z}/{x}/{y}.terrain"],
  "minzoom": 0,
  "maxzoom": 11,
  "bounds": [124.0, 33.0, 132.0, 43.0],
  "projection": "EPSG:4326",
  "extensions": ["octvertexnormals"],
  "available": [
    [{"startX":0,"startY":0,"endX":1,"endY":0}],
    [{"startX":0,"startY":0,"endX":3,"endY":1}],
    [{"startX":0,"startY":0,"endX":7,"endY":3}],
    [{"startX":0,"startY":0,"endX":15,"endY":7}],
    [{"startX":0,"startY":0,"endX":31,"endY":15}],
    [{"startX":0,"startY":0,"endX":63,"endY":31}],
    [{"startX":107,"startY":43,"endX":111,"endY":48}],
    [{"startX":215,"startY":86,"endX":222,"endY":96}],
    [{"startX":430,"startY":172,"endX":445,"endY":192}],
    [{"startX":861,"startY":344,"endX":890,"endY":384}],
    [{"startX":1723,"startY":688,"endX":1780,"endY":768}],
    [{"startX":3447,"startY":1376,"endX":3561,"endY":1536}]
  ]
})";

  _layerJsonCache.resize(json.size());
  std::memcpy(_layerJsonCache.data(), json.data(), json.size());
  spdlog::info(
      "[BlobAssetAccessor] layer.json generated ({} bytes)",
      json.size());
  return _layerJsonCache;
}

const std::vector<std::byte>& BlobAssetAccessor::getTileMapResourceXml() {
  if (!_tileMapXmlCache.empty())
    return _tileMapXmlCache;

  const std::string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                          "<TileMap version=\"1.0.0\" "
                          "tilemapservice=\"http://tms.osgeo.org/1.0.0\">\n"
                          "  <Title>Local Imagery</Title>\n"
                          "  <Abstract></Abstract>\n"
                          "  <SRS>EPSG:4326</SRS>\n"
                          "  <BoundingBox minx=\"124.0\" miny=\"33.0\" "
                          "maxx=\"132.0\" maxy=\"43.0\"/>\n"
                          "  <Origin x=\"-180.0\" y=\"-90.0\"/>\n"
                          "  <TileFormat width=\"512\" height=\"512\" "
                          "mime-type=\"image/png\" extension=\"png\"/>\n"
                          "  <TileSets profile=\"geodetic\">\n"
                          "    <TileSet href=\"6\"  "
                          "units-per-pixel=\"0.703125\"     order=\"6\"/>\n"
                          "    <TileSet href=\"7\"  "
                          "units-per-pixel=\"0.3515625\"    order=\"7\"/>\n"
                          "    <TileSet href=\"8\"  "
                          "units-per-pixel=\"0.17578125\"   order=\"8\"/>\n"
                          "    <TileSet href=\"9\"  "
                          "units-per-pixel=\"0.087890625\"  order=\"9\"/>\n"
                          "    <TileSet href=\"10\" "
                          "units-per-pixel=\"0.0439453125\" order=\"10\"/>\n"
                          "  </TileSets>\n"
                          "</TileMap>\n";

  _tileMapXmlCache.resize(xml.size());
  std::memcpy(_tileMapXmlCache.data(), xml.data(), xml.size());
  spdlog::info(
      "[BlobAssetAccessor] tilemapresource.xml generated ({} bytes)",
      xml.size());
  return _tileMapXmlCache;
}

// ============================================================
//  Constructor
// ============================================================
BlobAssetAccessor::BlobAssetAccessor(
    std::shared_ptr<CesiumAsync::IAssetAccessor> pFallback)
    : _pFallback(std::move(pFallback)) {
  spdlog::info("[BlobAssetAccessor] Initialized.");
}

// ============================================================
//  get() — main interception logic
// ============================================================
CesiumAsync::Future<std::shared_ptr<CesiumAsync::IAssetRequest>>
BlobAssetAccessor::get(
    const CesiumAsync::AsyncSystem& asyncSystem,
    const std::string& url,
    const std::vector<THeader>& headers) {

  spdlog::warn("[BlobAssetAccessor] get(): {}", url);

  std::string cleanUrl = stripQuery(url);

  // ---- Pattern 1: layer.json ----
  if (cleanUrl.find("layer.json") != std::string::npos) {
    const auto& data = getLayerJson();
    spdlog::info("[BlobAssetAccessor] Intercepted layer.json");
    auto req =
        std::make_shared<BlobAssetRequest>(url, data.data(), data.size());
    return asyncSystem.createResolvedFuture(
        std::shared_ptr<CesiumAsync::IAssetRequest>(std::move(req)));
  }

  // ---- Pattern 2: tilemapresource.xml ----
  if (cleanUrl.find("tilemapresource.xml") != std::string::npos) {
    const auto& data = getTileMapResourceXml();
    spdlog::info("[BlobAssetAccessor] Intercepted tilemapresource.xml");
    auto req =
        std::make_shared<BlobAssetRequest>(url, data.data(), data.size());
    return asyncSystem.createResolvedFuture(
        std::shared_ptr<CesiumAsync::IAssetRequest>(std::move(req)));
  }

  // ---- Pattern 3: .terrain tile ----
  {
    int z = 0, x = 0, y = 0;
    if (matchTerrainTile(url, z, x, y)) {
      auto [ptr, size] = tryGetFromTerrainBlob(z, x, y);
      if (ptr && size > 0) {
        spdlog::info(
            "[BlobAssetAccessor] Terrain HIT z={} x={} y={} size={}",
            z,
            x,
            y,
            size);
        auto req = std::make_shared<BlobAssetRequest>(url, ptr, size);
        return asyncSystem.createResolvedFuture(
            std::shared_ptr<CesiumAsync::IAssetRequest>(std::move(req)));
      } else {
        spdlog::warn(
            "[BlobAssetAccessor] Terrain MISS z={} x={} y={}",
            z,
            x,
            y);
      }
    }
  }

  // ---- Pattern 4: .png imagery tile ----
  {
    int z = 0, x = 0, y = 0;
    if (matchImageryTile(url, z, x, y)) {
      int blobIdx = -1;
      if (z == 6 || z == 7)
        blobIdx = 0;
      else if (z == 8)
        blobIdx = 1;
      else if (z == 9)
        blobIdx = 2;
      else if (z == 10)
        blobIdx = 3;

      spdlog::warn(
          "[BlobAssetAccessor] PNG match z={} x={} y={} -> blobIdx={} "
          "ptrNull={} blobSize={}",
          z,
          x,
          y,
          blobIdx,
          (blobIdx >= 0 ? g_imageryBlobPtrs[blobIdx] == nullptr : true),
          (blobIdx >= 0 ? g_imageryBlobSizes[blobIdx] : -1));

      auto [ptr, size] = tryGetFromImageryBlob(z, x, y);
      if (ptr && size > 0) {
        spdlog::info(
            "[BlobAssetAccessor] Imagery HIT z={} x={} y={} size={}",
            z,
            x,
            y,
            size);
        auto req = std::make_shared<BlobAssetRequest>(url, ptr, size);
        return asyncSystem.createResolvedFuture(
            std::shared_ptr<CesiumAsync::IAssetRequest>(std::move(req)));
      } else {
        spdlog::warn(
            "[BlobAssetAccessor] Imagery MISS z={} x={} y={}",
            z,
            x,
            y);
      }
    } else {
      if (cleanUrl.find(".png") != std::string::npos) {
        spdlog::error(
            "[BlobAssetAccessor] PNG URL detected but matchImageryTile FAILED: "
            "{}",
            cleanUrl);
      }
    }
  }

  // ---- Fallback ----
  spdlog::warn("[BlobAssetAccessor] Fallback -> HTTP: {}", url);
  return _pFallback->get(asyncSystem, url, headers);
}

// ============================================================
//  request() — always delegated
// ============================================================
CesiumAsync::Future<std::shared_ptr<CesiumAsync::IAssetRequest>>
BlobAssetAccessor::request(
    const CesiumAsync::AsyncSystem& asyncSystem,
    const std::string& verb,
    const std::string& url,
    const std::vector<THeader>& headers,
    const std::span<const std::byte>& contentPayload) {
  return _pFallback->request(asyncSystem, verb, url, headers, contentPayload);
}

void BlobAssetAccessor::tick() noexcept {}

} // namespace CesiumForUnityNative
