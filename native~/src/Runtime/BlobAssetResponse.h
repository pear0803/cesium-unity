#pragma once

#include <CesiumAsync/IAssetRequest.h>
#include <CesiumAsync/IAssetResponse.h>
#include <CesiumAsync/HttpHeaders.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>

namespace CesiumForUnityNative {

// ============================================================
//  BlobAssetResponse
//  Blob 메모리 영역을 HTTP 응답처럼 포장하는 클래스.
//  데이터 복사 없이 외부 포인터(span)를 직접 참조한다.
// ============================================================
class BlobAssetResponse : public CesiumAsync::IAssetResponse {
public:
  // ptr  : Blob 버퍼 내 타일 데이터 시작 포인터
  // size : 타일 데이터 크기 (bytes)
  // url  : 원본 요청 URL (Content-Type 결정에 사용)
  BlobAssetResponse(
      const std::byte* ptr,
      std::size_t size,
      std::string_view url)
      : _data(ptr, size),
        _contentType(resolveContentType(url)),
        _headers() {}

  // 항상 200 반환
  virtual uint16_t statusCode() const override {
    return 200;
  }

  // URL 확장자 기반 Content-Type 반환
  virtual std::string contentType() const override {
    return _contentType;
  }

  // 빈 헤더 맵 반환 (cesium-native는 headers()를 필수로 요구)
  virtual const CesiumAsync::HttpHeaders& headers() const override {
    return _headers;
  }

  // Blob 포인터를 span으로 직접 반환 — 복사 없음
  virtual std::span<const std::byte> data() const override {
    return _data;
  }

private:
  // URL 패턴에 따라 Content-Type 결정
  static std::string resolveContentType(std::string_view url) {
    // .terrain 확장자 → quantized-mesh
    if (url.size() >= 8 &&
        url.substr(url.size() - 8) == ".terrain") {
      return "application/vnd.quantized-mesh";
    }
    // .png 확장자 → 이미지
    if (url.size() >= 4 &&
        url.substr(url.size() - 4) == ".png") {
      return "image/png";
    }
    // layer.json
    if (url.find("layer.json") != std::string_view::npos) {
      return "application/json";
    }
    // tilemapresource.xml
    if (url.find("tilemapresource.xml") != std::string_view::npos) {
      return "application/xml";
    }
    // 그 외 fallback
    return "application/octet-stream";
  }

  std::span<const std::byte>  _data;         // Blob 직접 참조 (소유 X)
  std::string                 _contentType;  // 결정된 Content-Type
  CesiumAsync::HttpHeaders    _headers;      // 빈 헤더 맵
};

} // namespace CesiumForUnityNative
