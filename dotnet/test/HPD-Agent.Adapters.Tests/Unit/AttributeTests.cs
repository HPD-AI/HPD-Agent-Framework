using FluentAssertions;
using HPD.Agent.Adapters;

namespace HPD.Agent.Adapters.Tests.Unit;

/// <summary>
/// Tests for adapter attribute construction and property values.
/// Verifies that each attribute stores its arguments correctly and has the expected defaults.
/// </summary>
public class AttributeTests
{
    // ── HpdAdapterAttribute ───────────────────────────────────────────

    [Fact]
    public void HpdAdapterAttribute_StoresName()
    {
        var attr = new HpdAdapterAttribute("slack");

        attr.Name.Should().Be("slack");
    }

    [Fact]
    public void HpdAdapterAttribute_TargetsClass()
    {
        var usage = (AttributeUsageAttribute)typeof(HpdAdapterAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Single();

        usage.ValidOn.Should().HaveFlag(AttributeTargets.Class);
    }

    // ── HpdWebhookHandlerAttribute ────────────────────────────────────

    [Fact]
    public void HpdWebhookHandlerAttribute_StoresEventType()
    {
        var attr = new HpdWebhookHandlerAttribute("app_mention");

        attr.EventType.Should().Be("app_mention");
    }

    [Fact]
    public void HpdWebhookHandlerAttribute_AllowMultiple_IsTrue()
    {
        var usage = (AttributeUsageAttribute)typeof(HpdWebhookHandlerAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Single();

        usage.AllowMultiple.Should().BeTrue();
    }

    [Fact]
    public void HpdWebhookHandlerAttribute_TargetsMethod()
    {
        var usage = (AttributeUsageAttribute)typeof(HpdWebhookHandlerAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Single();

        usage.ValidOn.Should().HaveFlag(AttributeTargets.Method);
    }

    // ── HpdWebhookSignatureAttribute ──────────────────────────────────

    [Fact]
    public void HpdWebhookSignatureAttribute_StoresFormat()
    {
        var attr = new HpdWebhookSignatureAttribute(HmacFormat.V0TimestampBody);

        attr.Format.Should().Be(HmacFormat.V0TimestampBody);
    }

    [Fact]
    public void HpdWebhookSignatureAttribute_DefaultSignatureHeader_IsEmpty()
    {
        var attr = new HpdWebhookSignatureAttribute(HmacFormat.V0TimestampBody);

        attr.SignatureHeader.Should().BeEmpty();
    }

    [Fact]
    public void HpdWebhookSignatureAttribute_DefaultTimestampHeader_IsEmpty()
    {
        var attr = new HpdWebhookSignatureAttribute(HmacFormat.V0TimestampBody);

        attr.TimestampHeader.Should().BeEmpty();
    }

    [Fact]
    public void HpdWebhookSignatureAttribute_DefaultWindowSeconds_Is300()
    {
        var attr = new HpdWebhookSignatureAttribute(HmacFormat.V0TimestampBody);

        attr.WindowSeconds.Should().Be(300);
    }

    [Fact]
    public void HpdWebhookSignatureAttribute_NamedPropertiesOverrideDefaults()
    {
        var attr = new HpdWebhookSignatureAttribute(HmacFormat.V0TimestampBody)
        {
            SignatureHeader = "X-Sig",
            TimestampHeader = "X-TS",
            WindowSeconds   = 120,
        };

        attr.SignatureHeader.Should().Be("X-Sig");
        attr.TimestampHeader.Should().Be("X-TS");
        attr.WindowSeconds.Should().Be(120);
    }

    // ── HpdStreamingAttribute ─────────────────────────────────────────

    [Fact]
    public void HpdStreamingAttribute_StoresStrategy()
    {
        var attr = new HpdStreamingAttribute(StreamingStrategy.PostAndEdit);

        attr.Strategy.Should().Be(StreamingStrategy.PostAndEdit);
    }

    [Fact]
    public void HpdStreamingAttribute_DefaultDebounceMs_Is500()
    {
        var attr = new HpdStreamingAttribute(StreamingStrategy.PostAndEdit);

        attr.DebounceMs.Should().Be(500);
    }

    [Fact]
    public void HpdStreamingAttribute_CustomDebounceMs()
    {
        var attr = new HpdStreamingAttribute(StreamingStrategy.BufferAndPost)
        {
            DebounceMs = 250,
        };

        attr.DebounceMs.Should().Be(250);
    }

    [Fact]
    public void HpdStreamingAttribute_AllStrategiesAreValid()
    {
        var strategies = new[]
        {
            StreamingStrategy.PostAndEdit,
            StreamingStrategy.BufferAndPost,
            StreamingStrategy.Native,
        };

        foreach (var s in strategies)
        {
            var attr = new HpdStreamingAttribute(s);
            attr.Strategy.Should().Be(s);
        }
    }

    // ── HpdPermissionHandlerAttribute ────────────────────────────────

    [Fact]
    public void HpdPermissionHandlerAttribute_CanBeInstantiated()
    {
        var act = () => new HpdPermissionHandlerAttribute();

        act.Should().NotThrow();
    }

    [Fact]
    public void HpdPermissionHandlerAttribute_TargetsMethod()
    {
        var usage = (AttributeUsageAttribute)typeof(HpdPermissionHandlerAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Single();

        usage.ValidOn.Should().HaveFlag(AttributeTargets.Method);
    }

    // ── CardRendererAttribute ─────────────────────────────────────────

    [Fact]
    public void CardRendererAttribute_CanBeInstantiated()
    {
        var act = () => new CardRendererAttribute();

        act.Should().NotThrow();
    }

    // ── WebhookPayloadAttribute ───────────────────────────────────────

    [Fact]
    public void WebhookPayloadAttribute_CanBeInstantiated()
    {
        var act = () => new WebhookPayloadAttribute();

        act.Should().NotThrow();
    }

    // ── ThreadIdAttribute ─────────────────────────────────────────────

    [Fact]
    public void ThreadIdAttribute_StoresFormatString()
    {
        var attr = new ThreadIdAttribute("slack:{Channel}:{ThreadTs}");

        attr.Format.Should().Be("slack:{Channel}:{ThreadTs}");
    }

    // ── AdapterErrorsAttribute / ErrorCodeAttribute ───────────────────

    [Fact]
    public void AdapterErrorsAttribute_StoresAdapterName()
    {
        var attr = new AdapterErrorsAttribute("slack");

        attr.AdapterName.Should().Be("slack");
    }

    [Fact]
    public void ErrorCodeAttribute_StoresCodeAndExceptionType()
    {
        var attr = new ErrorCodeAttribute("not_in_channel", typeof(Exception));

        attr.Code.Should().Be("not_in_channel");
        attr.ExceptionType.Should().Be(typeof(Exception));
    }

    // ── PlatformFormatConverter format attributes ─────────────────────

    [Fact]
    public void BoldAttribute_StoresFormatString()
    {
        var attr = new BoldAttribute("*{0}*");

        attr.Format.Should().Be("*{0}*");
    }

    [Fact]
    public void ItalicAttribute_StoresFormatString()
    {
        var attr = new ItalicAttribute("_{0}_");

        attr.Format.Should().Be("_{0}_");
    }

    [Fact]
    public void StrikeAttribute_StoresFormatString()
    {
        var attr = new StrikeAttribute("~{0}~");

        attr.Format.Should().Be("~{0}~");
    }

    [Fact]
    public void LinkAttribute_StoresFormatString()
    {
        var attr = new LinkAttribute("<{1}|{0}>");

        attr.Format.Should().Be("<{1}|{0}>");
    }

    [Fact]
    public void CodeAttribute_StoresFormatString()
    {
        var attr = new CodeAttribute("`{0}`");

        attr.Format.Should().Be("`{0}`");
    }

    [Fact]
    public void CodeBlockAttribute_StoresFormatString()
    {
        var attr = new CodeBlockAttribute("```\n{0}\n```");

        attr.Format.Should().Be("```\n{0}\n```");
    }

    // ── HmacFormat enum ───────────────────────────────────────────────

    [Fact]
    public void HmacFormat_V0TimestampBody_HasExpectedValue()
    {
        // Ensure the enum is defined — the exact int value is an implementation detail
        Enum.IsDefined(typeof(HmacFormat), HmacFormat.V0TimestampBody).Should().BeTrue();
    }

    // ── StreamingStrategy enum ────────────────────────────────────────

    [Fact]
    public void StreamingStrategy_AllThreeValuesAreDefined()
    {
        Enum.IsDefined(typeof(StreamingStrategy), StreamingStrategy.PostAndEdit).Should().BeTrue();
        Enum.IsDefined(typeof(StreamingStrategy), StreamingStrategy.BufferAndPost).Should().BeTrue();
        Enum.IsDefined(typeof(StreamingStrategy), StreamingStrategy.Native).Should().BeTrue();
    }
}
