using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class ApiKeyProtectionTests
{
    [Fact]
    public void Protect_EncryptsAndUnprotectRestoresOriginal()
    {
        const string key = "sk-test-secret-value";

        string protectedValue = ApiKeyProtection.Protect(key);

        protectedValue.Should().NotBe(key);
        protectedValue.Should().StartWith("dpapi1:");
        ApiKeyProtection.Unprotect(protectedValue).Should().Be(key);
    }

    [Fact]
    public void Unprotect_LegacyPlainText_ReturnsSameValue()
    {
        const string key = "sk-legacy-plain-text";

        ApiKeyProtection.Unprotect(key).Should().Be(key);
    }

    [Fact]
    public void Protect_EmptyValue_ReturnsEmpty()
    {
        ApiKeyProtection.Protect(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Protect_AlreadyProtected_DoesNotDoubleEncrypt()
    {
        string protectedValue = ApiKeyProtection.Protect("sk-once");

        string twice = ApiKeyProtection.Protect(protectedValue);

        twice.Should().Be(protectedValue);
    }

    [Fact]
    public void Unprotect_InvalidCiphertext_ReturnsEmptyInsteadOfCiphertext()
    {
        // 修复回归：解密失败时绝不能再把密文（"dpapi1:..."）当 API Key 返回，
        // 否则会把它当 Bearer Token 发送导致服务端 401。
        const string corrupted = "dpapi1:not-valid-base64!!";

        string result = ApiKeyProtection.Unprotect(corrupted);

        result.Should().BeEmpty();
    }
}
