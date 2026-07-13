using System.Text;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

public class Base64UrlTests
{
    [Fact]
    public void Encode_is_url_safe_and_unpadded()
    {
        // 0xFF 0xFE 0xFD → standard base64 "//79" → url-safe "__79", no '=' padding.
        Assert.Equal("__79", Base64Url.Encode(new byte[] { 0xFF, 0xFE, 0xFD }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(65)]
    public void Encode_then_decode_round_trips(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 7 + 3);
        }

        Assert.Equal(bytes, Base64Url.Decode(Base64Url.Encode(bytes)));
    }

    [Fact]
    public void Decode_accepts_the_rfc8291_auth_secret_vector()
    {
        // auth_secret from RFC 8291 §5 is 16 bytes.
        Assert.Equal(16, Base64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg").Length);
    }

    [Fact]
    public void Decode_recovers_ascii_plaintext()
    {
        var decoded = Base64Url.Decode("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24");
        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.ASCII.GetString(decoded));
    }
}
