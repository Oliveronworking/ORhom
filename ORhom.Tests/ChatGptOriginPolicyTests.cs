namespace ORhom.Tests;

public sealed class ChatGptOriginPolicyTests
{
    [Theory]
    [InlineData("https://chatgpt.com")]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://chatgpt.com/c/123?model=test")]
    [InlineData("HTTPS://CHATGPT.COM/g/example")]
    public void ConfiguredOfficialUrlsAreAccepted(string url)
    {
        Assert.Equal(url, ChatGptOriginPolicy.NormalizeConfiguredUrl(url));
    }

    [Theory]
    [InlineData("chatgpt.com")]
    [InlineData("chatgpt.com/")]
    [InlineData("chatgpt.com/c/123?model=test")]
    [InlineData("https://chatgpt.com")]
    public void OfficialOmniboxUrlsAreAccepted(string url)
    {
        Assert.True(ChatGptOriginPolicy.IsAllowedObservedUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://chatgpt.com")]
    [InlineData("https://chatgpt.com.evil.example")]
    [InlineData("https://chatgpt.com@evil.example")]
    [InlineData("https://chatgpt.com:444")]
    [InlineData("file:///C:/temp/page.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://chаtgpt.com")]
    [InlineData("https://www.chatgpt.com")]
    public void ConfiguredUntrustedOriginsFallBackToOfficialDefault(string url)
    {
        Assert.Equal(
            ChatGptOriginPolicy.DefaultUrl,
            ChatGptOriginPolicy.NormalizeConfiguredUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Search Google or type a URL")]
    [InlineData("chatgpt.com.evil.example")]
    [InlineData("chatgpt.com@evil.example")]
    [InlineData("http://chatgpt.com")]
    [InlineData("https://chatgpt.com:444")]
    public void UntrustedOrUnknownOmniboxOriginsAreRejected(string? url)
    {
        Assert.False(ChatGptOriginPolicy.IsAllowedObservedUrl(url));
    }
}
