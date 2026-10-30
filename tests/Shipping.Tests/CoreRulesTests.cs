using Microsoft.Extensions.Options;
using Shipping.BookingService;
using Shipping.Contracts;
using Shipping.PaymentService;
using Xunit;

namespace Shipping.Tests;

public class PricingTests
{
    [Theory]
    [InlineData(1.0, "STANDARD", 7400L)]     // 4900 + 1 kg * 2500
    [InlineData(2.0, "EXPRESS", 17820L)]     // (4900 + 2 * 2500) * 1.8
    [InlineData(2.0, null, 9900L)]           // default = standard
    public void Calculates_price_in_paise(double kg, string? level, long expected) =>
        Assert.Equal(expected, Pricing.Calculate((decimal)kg, level));

    [Fact]
    public void Rejects_invalid_requests()
    {
        var errors = new CreateShipmentRequest("", "Mumbai", "A", 0, "TELEPORT").Validate();
        Assert.Contains("Origin", errors.Keys);
        Assert.Contains("WeightKg", errors.Keys);
        Assert.Contains("ServiceLevel", errors.Keys);
    }
}

public class RazorpaySignatureTests
{
    private static RazorpayGateway Gateway() => new(new HttpClient(), Options.Create(new RazorpayOptions
    {
        KeyId = "rzp_test_x", KeySecret = "key-secret", WebhookSecret = "hook-secret",
    }));

    [Fact]
    public void Accepts_valid_checkout_signature()
    {
        var sig = Signatures.HexHmac("key-secret", "order_1|pay_1");
        Assert.True(Gateway().VerifyPaymentSignature("order_1", "pay_1", sig));
    }

    [Fact]
    public void Rejects_tampered_checkout_signature()
    {
        var sig = Signatures.HexHmac("key-secret", "order_1|pay_1");
        Assert.False(Gateway().VerifyPaymentSignature("order_1", "pay_OTHER", sig));
        Assert.False(Gateway().VerifyPaymentSignature("order_1", "pay_1", ""));
    }

    [Fact]
    public void Verifies_webhook_signature_over_the_raw_body()
    {
        const string body = "{\"event\":\"payment.captured\"}";
        Assert.True(Gateway().VerifyWebhookSignature(body, Signatures.HexHmac("hook-secret", body)));
        Assert.False(Gateway().VerifyWebhookSignature(body + " ", Signatures.HexHmac("hook-secret", body)));
    }
}

public class StatusRuleTests
{
    [Theory]
    [InlineData("CONFIRMED", "PICKED_UP", true)]
    [InlineData("PICKED_UP", "IN_TRANSIT", true)]
    [InlineData("BOOKED", "PICKED_UP", false)]          // not paid yet
    [InlineData("PAYMENT_FAILED", "PICKED_UP", false)]
    [InlineData("IN_TRANSIT", "PICKED_UP", false)]      // no going backwards
    [InlineData("CONFIRMED", "CONFIRMED", false)]
    [InlineData("CONFIRMED", "NONSENSE", false)]
    public void Carrier_transitions(string current, string next, bool allowed) =>
        Assert.Equal(allowed, ShipmentStatuses.CanCarrierAdvance(current, next));
}
