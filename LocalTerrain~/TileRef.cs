using System;

namespace LocalTerrain
{
    /// <summary>
    /// Blob 내 단일 타일의 위치 정보.
    /// BlobIndex.TryGetTerrain / TryGetImagery 가 반환합니다.
    /// </summary>
    public readonly struct TileRef
    {
        /// <summary>이 타일이 속한 Blob의 byte[] 전체</summary>
        public readonly byte[] Blob;

        /// <summary>Blob 내 타일 데이터 시작 오프셋 (byte 단위)</summary>
        public readonly int Offset;

        /// <summary>타일 데이터 크기 (byte 단위)</summary>
        public readonly int Size;

        public TileRef(byte[] blob, int offset, int size)
        {
            Blob = blob;
            Offset = offset;
            Size = size;
        }

        /// <summary>
        /// 타일 데이터를 ReadOnlySpan으로 반환합니다 (복사 없음).
        /// C# 8+ 에서 사용 가능.
        /// </summary>
        public ReadOnlySpan<byte> AsSpan() => new ReadOnlySpan<byte>(Blob, Offset, Size);

        /// <summary>
        /// 타일 데이터를 새 byte[]로 복사하여 반환합니다.
        /// 꼭 필요한 경우에만 사용하세요 (힙 할당 발생).
        /// </summary>
        public byte[] ToArray()
        {
            var buf = new byte[Size];
            Buffer.BlockCopy(Blob, Offset, buf, 0, Size);
            return buf;
        }

        public bool IsValid => Blob != null && Size > 0;

        public override string ToString() =>
            $"TileRef(Offset={Offset}, Size={Size}, BlobLen={Blob?.Length ?? 0})";
    }
}