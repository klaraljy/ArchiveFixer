using System;
using System.Security.Cryptography;

namespace ArchiveFixer.Extraction;

/// <summary>
/// WinZip AES 的 AES-CTR 密钥流（一次一个块，块号从 1 开始）。
///
/// <para>为什么不用 <c>Aes</c> 的 CBC/CFB 模式凑：WinZip 的 CTR 是**自定义计数器布局**
/// （低 4 字节小端块号 + 12 字节 0），只有 ECB 加密计数器块这一条路能原样实现它。</para>
/// </summary>
internal sealed class ZipAesCtrTransform : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _keystreamOffset = 16;
    private uint _blockIndex;

    public ZipAesCtrTransform(byte[] encryptionKey)
    {
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = encryptionKey;
        _ecb = _aes.CreateEncryptor();
    }

    /// <summary>把 <paramref name="count"/> 个字节就地异或掉（输入 = 密文，输出 = 明文）。</summary>
    public void Transform(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (_keystreamOffset >= 16)
            {
                NextKeystreamBlock();
            }

            buffer[offset + i] ^= _keystream[_keystreamOffset++];
        }
    }

    private void NextKeystreamBlock()
    {
        _blockIndex++;

        // 低 4 字节小端记块号，其余 12 字节保持 0（规范布局，见类注释）。
        _counter[0] = (byte)(_blockIndex & 0xFF);
        _counter[1] = (byte)((_blockIndex >> 8) & 0xFF);
        _counter[2] = (byte)((_blockIndex >> 16) & 0xFF);
        _counter[3] = (byte)((_blockIndex >> 24) & 0xFF);

        _ecb.TransformBlock(_counter, 0, 16, _keystream, 0);
        _keystreamOffset = 0;
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }
}
