using System;
using System.Security.Cryptography;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// AES-256-CTR.
    ///
    /// Почему свой: в .NET Framework BCL есть только CBC/ECB/CFB. Режима CTR нет,
    /// а MTProto-обфускация построена именно на нём.
    ///
    /// КРИТИЧНО: инкремент счётчика — 128-битный big-endian по всему блоку,
    /// а не по младшим 32 битам. Иначе keystream разойдётся с эталоном уже
    /// на втором блоке, и handshake «просто не работает» без внятной ошибки.
    ///
    /// Производительность: шифруем пачками по 64 блока (1 КБ) за вызов
    /// TransformBlock, а не по 16 байт — иначе на 100 Мбит/с это станет
    /// единственным узким местом.
    /// </summary>
    public sealed class AesCtr : IDisposable
    {
        private const int BlockSize = 16;
        private const int BatchBlocks = 64;

        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _counter = new byte[BlockSize];
        private readonly byte[] _keystream = new byte[BlockSize * BatchBlocks];
        private readonly byte[] _counterBatch = new byte[BlockSize * BatchBlocks];

        private int _keystreamOffset = BlockSize * BatchBlocks;   // «пусто» при старте
        private bool _disposed;

        public AesCtr(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("AES-256 требует ключ 32 байта", nameof(key));
            if (iv == null || iv.Length != 16)
                throw new ArgumentException("IV должен быть 16 байт", nameof(iv));

            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;              // CTR строится поверх ECB
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;

            _ecb = _aes.CreateEncryptor();
            Buffer.BlockCopy(iv, 0, _counter, 0, BlockSize);
        }

        /// <summary>Зашифровать/расшифровать на месте (CTR симметричен).</summary>
        public void Transform(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AesCtr));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count <= 0) return;

            int pos = offset;
            int end = offset + count;

            while (pos < end)
            {
                // при необходимости генерируем новую пачку keystream
                if (_keystreamOffset >= _keystream.Length)
                {
                    FillKeystream();
                    _keystreamOffset = 0;
                }

                int available = _keystream.Length - _keystreamOffset;
                int take = Math.Min(available, end - pos);

                for (int i = 0; i < take; i++)
                    buffer[pos + i] ^= _keystream[_keystreamOffset + i];

                _keystreamOffset += take;
                pos += take;
            }
        }

        public byte[] Transform(byte[] data)
        {
            byte[] copy = (byte[])data.Clone();
            Transform(copy, 0, copy.Length);
            return copy;
        }

        private void FillKeystream()
        {
            int blocks = BatchBlocks;

            for (int i = 0; i < blocks; i++)
            {
                Buffer.BlockCopy(_counter, 0, _counterBatch, i * BlockSize, BlockSize);
                IncrementCounter();
            }

            _ecb.TransformBlock(_counterBatch, 0, _counterBatch.Length, _keystream, 0);
        }

        /// <summary>128-битный big-endian инкремент. Не «младшие 4 байта»!</summary>
        private void IncrementCounter()
        {
            for (int i = BlockSize - 1; i >= 0; i--)
            {
                if (++_counter[i] != 0) return;   // переполнение байта -> перенос дальше
            }
        }

        /// <summary>Пропустить N байт keystream (нужно для MTProto: первые 64 байта «проглатываются»).</summary>
        public void Skip(int bytes)
        {
            if (bytes <= 0) return;

            byte[] scratch = new byte[Math.Min(bytes, 4096)];
            int left = bytes;

            while (left > 0)
            {
                int take = Math.Min(scratch.Length, left);
                Transform(scratch, 0, take);
                left -= take;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _ecb.Dispose(); } catch { }
            try { _aes.Dispose(); } catch { }
            Array.Clear(_counter, 0, _counter.Length);
            Array.Clear(_keystream, 0, _keystream.Length);
            Array.Clear(_counterBatch, 0, _counterBatch.Length);
        }
    }
}
