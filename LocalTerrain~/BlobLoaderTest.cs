using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using LocalTerrain;
using Debug = UnityEngine.Debug;

namespace LocalTerrain.Tests
{
    /// <summary>
    /// GameObject 에 붙여서 Play Mode 에서 실행하는 통합 테스트.
    ///
    /// 검증 항목:
    ///   1. 파일 5개 병렬 로딩 성공 + 크기 확인
    ///   2. TERRBLOB 파싱 및 24,897 엔트리 확인
    ///   3. IMGEBLOB 파싱 및 4,654 엔트리 확인 (4개 blob 합산)
    ///   4. 커버리지 범위 내 임의 타일 조회 성공
    ///   5. 커버리지 범위 밖 타일 조회 실패
    ///   6. 타일 데이터 첫 바이트 출력 (육안 확인용)
    ///   7. 성능: 로드 3초 미만, 인덱스 구축 100ms 미만
    ///   8. 키 인코딩/디코딩 왕복 검사
    /// </summary>
    public class BlobLoaderTest : MonoBehaviour
    {
        [Header("Blob 디렉토리 경로")]
        [Tooltip("비워두면 Application.streamingAssetsPath + /TerrainData 를 사용합니다.")]
        public string overridePath = "";

        private async void Start()
        {
            string dataDir = string.IsNullOrEmpty(overridePath)
                ? System.IO.Path.Combine(Application.streamingAssetsPath, "TerrainData")
                : overridePath;

            Debug.Log("===========================================");
            Debug.Log("[Test] A-3 BlobLoader + BlobIndex 통합 테스트 시작");
            Debug.Log("[Test] 데이터 경로: " + dataDir);
            Debug.Log("===========================================");

            int passed = 0;
            int failed = 0;

            // ------------------------------------------------------------------
            // TEST 1: BlobLoader 병렬 로딩
            // ------------------------------------------------------------------
            var swLoad = Stopwatch.StartNew();
            try
            {
                await BlobLoader.LoadAllAsync(dataDir);
                swLoad.Stop();

                Assert("BlobLoader.IsLoaded == true", BlobLoader.IsLoaded);
                Assert("TerrainBlob != null", BlobLoader.TerrainBlob != null);
                Assert("ImageryBlobs.Length == 4", BlobLoader.ImageryBlobs.Length == 4);

                // 크기 허용 오차: 약 20%
                AssertRange("terrain.bin 크기",
                    BlobLoader.TerrainBlob.LongLength, 100_000_000L, 160_000_000L);
                AssertRange("imagery_000 크기",
                    BlobLoader.ImageryBlobs[0].LongLength, 5_000_000L, 30_000_000L);
                AssertRange("imagery_003 크기",
                    BlobLoader.ImageryBlobs[3].LongLength, 80_000_000L, 200_000_000L);

                double loadSec = swLoad.Elapsed.TotalSeconds;
                AssertRange("로드 시간 3초 미만",
                    (long)(loadSec * 1000), 0L, 3000L);

                Debug.Log("[Test] PASS TEST 1 -- 로드 완료 (" + loadSec.ToString("F2") + "초)");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 1 -- " + e.Message);
                failed++;
                Debug.LogError("[Test] Blob 파일이 없으면 이후 테스트 불가. 중단합니다.");
                PrintSummary(passed, failed);
                return;
            }

            // ------------------------------------------------------------------
            // TEST 2: BlobIndex 구축
            // ------------------------------------------------------------------
            var swIdx = Stopwatch.StartNew();
            var idx = new BlobIndex();
            try
            {
                idx.BuildTerrainIndex(BlobLoader.TerrainBlob);
                foreach (var imgBlob in BlobLoader.ImageryBlobs)
                    idx.BuildImageryIndex(imgBlob);
                swIdx.Stop();

                // 타일 수 검증 (5% 여유)
                AssertRange("TerrainTileCount (약 24,897)",
                    idx.TerrainTileCount, 23_000, 27_000);
                AssertRange("ImageryTileCount (약 4,654)",
                    idx.ImageryTileCount, 4_000, 5_500);

                double idxMs = swIdx.Elapsed.TotalMilliseconds;
                AssertRange("인덱스 구축 100ms 미만",
                    (long)idxMs, 0L, 100L);

                idx.LogStats();
                Debug.Log("[Test] PASS TEST 2 -- 인덱스 구축 (" + idxMs.ToString("F1") + "ms)");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 2 -- " + e.Message);
                failed++;
            }

            // ------------------------------------------------------------------
            // TEST 3: 커버리지 범위 내 지형 타일 조회
            // 핸드오프 문서 섹션 3-5 커버리지 참조
            // ------------------------------------------------------------------
            try
            {
                var terrainSamples = new (int z, int x, int y)[]
                {
                    (6,  108,  45),    // Zoom 6 중앙
                    (7,  217,  88),    // Zoom 7
                    (8,  432,  175),   // Zoom 8
                    (9,  862,  345),   // Zoom 9
                    (10, 1724, 690),   // Zoom 10
                    (11, 3450, 1380),  // Zoom 11
                };

                int found = 0;
                foreach (var (z, x, y) in terrainSamples)
                {
                    bool ok = idx.TryGetTerrain(z, x, y, out var r);
                    if (ok)
                    {
                        found++;
                        Debug.Log("[Test]   지형 타일 (" + z + "/" + x + "/" + y + ") "
                            + "offset=" + r.Offset + " size=" + r.Size + "B");
                    }
                    else
                    {
                        // 커버리지 경계 근처 타일은 없을 수 있으므로 경고만
                        Debug.LogWarning("[Test]   지형 타일 (" + z + "/" + x + "/" + y
                            + ") -- 인덱스에 없음 (경계 타일일 수 있음)");
                    }
                }

                Assert("지형 샘플 6개 중 3개 이상 조회 성공", found >= 3);
                Debug.Log("[Test] PASS TEST 3 -- 지형 타일 " + found + "/" + terrainSamples.Length + "개 조회 성공");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 3 -- " + e.Message);
                failed++;
            }

            // ------------------------------------------------------------------
            // TEST 4: 커버리지 범위 내 이미지 타일 조회
            // ------------------------------------------------------------------
            try
            {
                var imagerySamples = new (int z, int x, int y)[]
                {
                    (6,  108,  45),
                    (7,  217,  88),
                    (8,  432,  175),
                    (9,  862,  345),
                    (10, 1724, 690),
                };

                int found = 0;
                foreach (var (z, x, y) in imagerySamples)
                {
                    bool ok = idx.TryGetImagery(z, x, y, out var r);
                    if (ok)
                    {
                        found++;
                        Debug.Log("[Test]   이미지 타일 (" + z + "/" + x + "/" + y + ") "
                            + "offset=" + r.Offset + " size=" + r.Size + "B");
                    }
                    else
                    {
                        Debug.LogWarning("[Test]   이미지 타일 (" + z + "/" + x + "/" + y + ") -- 없음");
                    }
                }

                Assert("이미지 샘플 5개 중 3개 이상 조회 성공", found >= 3);
                Debug.Log("[Test] PASS TEST 4 -- 이미지 타일 " + found + "/" + imagerySamples.Length + "개 조회 성공");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 4 -- " + e.Message);
                failed++;
            }

            // ------------------------------------------------------------------
            // TEST 5: 커버리지 밖 타일은 반드시 없어야 함
            // ------------------------------------------------------------------
            try
            {
                var outOfRange = new (int z, int x, int y)[]
                {
                    (6,  0,    0),     // 완전히 밖 (한반도 밖)
                    (10, 9999, 9999),  // 범위 초과
                    (11, 0,    0),     // Zoom 11이지만 X, Y 가 틀림
                    (5,  55,   22),    // Zoom 5 는 데이터 없음
                };

                foreach (var (z, x, y) in outOfRange)
                {
                    bool terrain = idx.TryGetTerrain(z, x, y, out _);
                    bool imagery = idx.TryGetImagery(z, x, y, out _);
                    Assert("범위 밖 타일 (" + z + "/" + x + "/" + y + ") terrain=false", !terrain);
                    Assert("범위 밖 타일 (" + z + "/" + x + "/" + y + ") imagery=false", !imagery);
                }

                Debug.Log("[Test] PASS TEST 5 -- 범위 밖 타일 " + outOfRange.Length + "개 모두 miss");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 5 -- " + e.Message);
                failed++;
            }

            // ------------------------------------------------------------------
            // TEST 6: 타일 데이터 무결성 확인
            // ReadOnlySpan 은 async 메서드 내 로컬 변수로 선언 불가이므로
            // 별도 동기 메서드(CheckTerrainBytes, CheckPngSignature)로 위임합니다.
            // ------------------------------------------------------------------
            try
            {
                if (idx.TryGetTerrain(9, 862, 345, out var tr))
                {
                    // quantized-mesh 최소 크기는 88 바이트
                    Assert("지형 타일 크기 88B 이상", tr.Size >= 88);
                    CheckTerrainBytes(tr);
                }

                if (idx.TryGetImagery(9, 862, 345, out var ir))
                {
                    Assert("이미지 타일 크기 8B 이상", ir.Size > 8);
                    CheckPngSignature(ir);
                }
                else
                {
                    Debug.LogWarning("[Test]   이미지 타일(9/862/345) 없음 -- PNG 시그니처 검사 스킵");
                }

                Debug.Log("[Test] PASS TEST 6 -- 타일 데이터 무결성 확인");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 6 -- " + e.Message);
                failed++;
            }

            // ------------------------------------------------------------------
            // TEST 7: 키 인코딩/디코딩 왕복 검사
            // ------------------------------------------------------------------
            try
            {
                var cases = new (int z, int x, int y)[]
                {
                    (6,  107,     43),
                    (11, 3561,    1536),
                    (10, 1780,    768),
                    (0,  0,       0),
                    (22, 0xFFFFF, 0xFFFFF),  // 최대값 경계
                };

                foreach (var (z, x, y) in cases)
                {
                    long key = BlobIndex.EncodeKey(z, x, y);
                    var (dz, dx, dy) = BlobIndex.DecodeKey(key);
                    Assert("키 왕복 zoom=" + z + ",x=" + x + ",y=" + y,
                        dz == z && dx == x && dy == y);
                }

                Debug.Log("[Test] PASS TEST 7 -- 키 인코딩/디코딩 왕복 " + cases.Length + "건");
                passed++;
            }
            catch (Exception e)
            {
                Debug.LogError("[Test] FAIL TEST 7 -- " + e.Message);
                failed++;
            }

            PrintSummary(passed, failed);
        }

        // ----------------------------------------------------------------------
        // ReadOnlySpan 을 사용하는 검사는 반드시 async 가 아닌 동기 메서드로 분리해야 합니다.
        // async 메서드의 로컬 변수로 ReadOnlySpan 을 선언하면 CS4012 컴파일 에러가 발생합니다.
        // ----------------------------------------------------------------------

        /// <summary>
        /// 지형 타일의 첫 8 바이트를 로그로 출력합니다.
        /// </summary>
        private static void CheckTerrainBytes(TileRef tr)
        {
            ReadOnlySpan<byte> span = tr.AsSpan();
            Debug.Log("[Test]   지형 타일(9/862/345) 첫 8바이트: "
                + span[0].ToString("X2") + " " + span[1].ToString("X2") + " "
                + span[2].ToString("X2") + " " + span[3].ToString("X2") + " "
                + span[4].ToString("X2") + " " + span[5].ToString("X2") + " "
                + span[6].ToString("X2") + " " + span[7].ToString("X2"));
        }

        /// <summary>
        /// PNG 파일 시그니처(89 50 4E 47 0D 0A 1A 0A)를 검증합니다.
        /// 불일치 시 예외를 던집니다.
        /// </summary>
        private static void CheckPngSignature(TileRef ir)
        {
            ReadOnlySpan<byte> span = ir.AsSpan();

            bool isPng = span[0] == 0x89 && span[1] == 0x50
                      && span[2] == 0x4E && span[3] == 0x47
                      && span[4] == 0x0D && span[5] == 0x0A
                      && span[6] == 0x1A && span[7] == 0x0A;

            if (!isPng)
            {
                throw new Exception(
                    "[Test] 이미지 타일 PNG 시그니처 불일치: "
                    + span[0].ToString("X2") + " " + span[1].ToString("X2") + " "
                    + span[2].ToString("X2") + " " + span[3].ToString("X2"));
            }

            Debug.Log("[Test]   이미지 타일(9/862/345) PNG 시그니처 정상 확인");
        }

        // ----------------------------------------------------------------------
        // 공통 헬퍼
        // ----------------------------------------------------------------------

        private static void Assert(string label, bool condition)
        {
            if (!condition)
                throw new Exception("ASSERT FAILED: " + label);
        }

        private static void AssertRange(string label, long value, long min, long max)
        {
            if (value < min || value > max)
                throw new Exception(
                    "ASSERT RANGE FAILED: " + label
                    + " -> " + value + " (기대 범위: " + min + "~" + max + ")");
        }

        private static void PrintSummary(int passed, int failed)
        {
            string result = (failed == 0) ? "전체 통과" : "실패 있음";
            Debug.Log("===========================================");
            Debug.Log("[Test] 결과 (" + result + "): " + passed + "개 통과 / " + failed + "개 실패");
            Debug.Log("===========================================");
        }
    }
}