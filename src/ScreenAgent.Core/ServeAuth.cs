using System.Security.Cryptography;
using System.Text;
namespace ScreenAgent.Core;
public static class ServeAuth
{
    public static bool ValidToken(string? token)=>token is {Length:>=16}&&!string.IsNullOrWhiteSpace(token);
    public static bool Authorized(string token,string? authorization)
        =>ValidToken(token)&&authorization is not null&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes("Bearer "+token)),SHA256.HashData(Encoding.UTF8.GetBytes(authorization)));
    public static bool JsonContentType(string? type)=>type?.Split(';')[0].Trim().Equals("application/json",StringComparison.OrdinalIgnoreCase)==true;
}
