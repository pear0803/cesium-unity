#pragma once

#include "BlobAssetResponse.h"

#include <CesiumAsync/IAssetRequest.h>
#include <CesiumAsync/IAssetResponse.h>
#include <CesiumAsync/HttpHeaders.h>

#include <cstddef>
#include <memory>
#include <string>

namespace CesiumForUnityNative {

// ============================================================
//  BlobAssetRequest
//  Blob 데이터를 HTTP 요청/응답 쌍처럼 포장하는 클래스.
//  cesium-native가 IAssetAccessor::get() 의 반환값으로
//  shared_ptr<IAssetRequest> 를 요구하므로 이 클래스가 필요.
// ============================================================
class BlobAssetRequest : public CesiumAsync::IAssetRequest {
public:
  // url  : 원본 요청 URL ("http://local-blob/6/108/45.terrain" 등)
  // ptr  : Blob 버퍼 내 타일 시작 포인터
  // size : 타일 데이터 크기 (bytes)
  BlobAssetRequest(
      std::string url,
      const std::byte* ptr,
      std::size_t size)
      : _method("GET"),
        _url(std::move(url)),
        _headers(),
        _response(std::make_unique<BlobAssetResponse>(ptr, size, _url)) {}

  // "GET" 고정
  virtual const std::string& method() const override {
    return _method;
  }

  // 원본 요청 URL 반환
  virtual const std::string& url() const override {
    return _url;
  }

  // 빈 헤더 맵 반환
  virtual const CesiumAsync::HttpHeaders& headers() const override {
    return _headers;
  }

  // BlobAssetResponse 포인터 반환
  // 이미 데이터가 준비되어 있으므로 nullptr 이 아님을 보장
  virtual const CesiumAsync::IAssetResponse* response() const override {
    return _response.get();
  }

private:
  std::string                       _method;    // 항상 "GET"
  std::string                       _url;       // 원본 요청 URL
  CesiumAsync::HttpHeaders          _headers;   // 빈 헤더 맵
  std::unique_ptr<BlobAssetResponse> _response; // 응답 객체 소유
};

} // namespace CesiumForUnityNative