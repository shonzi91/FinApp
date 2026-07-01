using System.Security.Cryptography;
using System.Text.Json;
using FinApp.Server.BankSync;

namespace FinApp.Server.Tests;

/// <summary>
/// Enable Banking's app auth signs a fresh RS256 JWT per request. IdentityModel caches signature providers by
/// key id, so a second call with the same application id must not reuse a provider whose RSA was already
/// disposed (that regressed as an ObjectDisposedException / HTTP 500 on "Link Revolut" in prod).
/// </summary>
public class EnableBankingJwtTests
{
    [Fact]
    public void Signs_repeatedly_with_the_same_application_id()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        const string appId = "0f3060b1-e197-4bfb-ac47-6039d3d22afa";

        var first = EnableBankingClient.BuildJwt(appId, pem);
        var second = EnableBankingClient.BuildJwt(appId, pem);   // must not throw ObjectDisposedException

        Assert.False(string.IsNullOrEmpty(first));
        Assert.False(string.IsNullOrEmpty(second));
        Assert.Equal(3, first.Split('.').Length);   // header.payload.signature
    }
}

/// <summary>
/// The transactions parser must cope with two provider JSON conventions: Berlin Group / NextGenPSD2 camelCase
/// (signed amounts, no debit/credit indicator, transactions nested under "booked") and Enable Banking's
/// snake_case native shape (unsigned amounts + a creditDebitIndicator, flat array). These guard both.
/// </summary>
public class BankTransactionParsingTests
{
    private static List<BankTransaction> Parse(string json) =>
        EnableBankingClient.ParseTransactions(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parses_berlin_group_camelcase_with_signed_amounts()
    {
        // Debit carries a negative sign and no indicator; the "booked" nesting mirrors the balance sample the user shared.
        var json = """
        {
          "transactions": {
            "booked": [
              {
                "transactionId": "tx-1",
                "bookingDate": "2026-06-28",
                "transactionAmount": { "currency": "EUR", "amount": "-61.52" },
                "remittanceInformationUnstructured": "TESCO STORES"
              },
              {
                "transactionId": "tx-2",
                "bookingDate": "2026-06-27",
                "transactionAmount": { "currency": "EUR", "amount": "100.00" },
                "creditorName": "ACME PAYROLL"
              }
            ]
          }
        }
        """;

        var txns = Parse(json);

        Assert.Equal(2, txns.Count);
        var debit = txns.Single(t => t.ExternalId == "tx-1");
        Assert.Equal(-61.52m, debit.Amount);            // sign preserved from the amount string
        Assert.Equal(new DateOnly(2026, 6, 28), debit.Date);
        Assert.Equal("TESCO STORES", debit.Description);
        Assert.Equal(100.00m, txns.Single(t => t.ExternalId == "tx-2").Amount);
    }

    [Fact]
    public void Parses_enable_banking_snakecase_with_indicator()
    {
        // Unsigned amount + creditDebitIndicator; flat array under "transactions".
        var json = """
        {
          "transactions": [
            {
              "entry_reference": "e-9",
              "booking_date": "2026-06-20",
              "transaction_amount": { "currency": "EUR", "amount": "12.30" },
              "credit_debit_indicator": "DBIT",
              "remittance_information": ["COFFEE", "SHOP"]
            }
          ]
        }
        """;

        var txns = Parse(json);

        var t = Assert.Single(txns);
        Assert.Equal("e-9", t.ExternalId);
        Assert.Equal(-12.30m, t.Amount);                // DBIT makes the unsigned amount negative
        Assert.Equal("COFFEE SHOP", t.Description);
    }

    [Fact]
    public void Synthesizes_a_stable_id_when_reference_is_missing()
    {
        var json = """
        {
          "transactions": { "booked": [
            { "bookingDate": "2026-06-01", "transactionAmount": { "currency": "EUR", "amount": "-5.00" } }
          ] }
        }
        """;

        var first = Parse(json).Single().ExternalId;
        var again = Parse(json).Single().ExternalId;

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, again);                     // deterministic, so re-syncs dedupe
    }

    [Fact]
    public void Empty_or_absent_transactions_yield_no_rows()
    {
        Assert.Empty(Parse("""{ "transactions": { "booked": [] } }"""));
        Assert.Empty(Parse("""{ "balances": [] }"""));
    }
}
