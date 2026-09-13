using System.Buffers.Text;
using System.Security.Cryptography;

namespace Samizdat.Server.Auth;

public static class ShareToken
{
    /// 16 байт: подобрать такой токен нельзя, а урл остаётся коротким и переживает копирование
    /// из мессенджера — в base64url нет символов, которые ломают разбор ссылки.
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
