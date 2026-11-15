using App.Core.Entities;
using App.Infrastructure.Workers;
using SharedKernel.Events;
using Xunit;

namespace App.Tests;

/// <summary>Unit tests for the outbox backoff schedule and the claim selection rules.</summary>
public class OutboxPoliciesTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(6, 160)]
    [InlineData(7, 300)] // 320 s, capped at 5 minutes
    [InlineData(1000, 300)]
    public void DelayAfterFailure_IsExponentialAndCapped(int retryCount, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxRetryPolicy.DelayAfterFailure(retryCount));
    }

    [Fact]
    public void DelayAfterFailure_NeverDecreasesAsFailuresGrow()
    {
        var previous = TimeSpan.Zero;
        for (var n = 1; n <= 50; n++)
        {
            var delay = OutboxRetryPolicy.DelayAfterFailure(n);
            Assert.True(delay >= previous);
            Assert.True(delay <= OutboxRetryPolicy.MaxDelay);
            previous = delay;
        }
    }

    [Fact]
    public void SelectClaimable_TakesDueRowsInOrderUpToBatchSize()
    {
        var now = DateTime.UtcNow.AddSeconds(1); // rows are due from their creation time
        var rows = Enumerable.Range(0, 5).Select(_ => NewRow(Guid.NewGuid())).ToList();

        var claimed = OutboxClaimPolicy.SelectClaimable(rows, now, batchSize: 3);

        Assert.Equal(rows.Take(3), claimed);
    }

    [Fact]
    public void SelectClaimable_SkipsLeasedAndBackingOffRows()
    {
        var now = DateTime.UtcNow.AddSeconds(1); // rows are due from their creation time
        var leased = NewRow(Guid.NewGuid());
        leased.Lease(now.AddMinutes(2));
        var backingOff = NewRow(Guid.NewGuid());
        backingOff.RecordFailure(now.AddSeconds(30));
        var due = NewRow(Guid.NewGuid());

        var claimed = OutboxClaimPolicy.SelectClaimable([leased, backingOff, due], now, batchSize: 10);

        Assert.Equal([due], claimed);
    }

    [Fact]
    public void SelectClaimable_EarlierLeasedRowBlocksLaterRowOfSameApplication()
    {
        var now = DateTime.UtcNow.AddSeconds(1); // rows are due from their creation time
        var applicationId = Guid.NewGuid();
        var submitted = NewRow(applicationId);
        submitted.Lease(now.AddMinutes(2)); // another replica holds it
        var statusChanged = NewRow(applicationId);

        var claimed = OutboxClaimPolicy.SelectClaimable([submitted, statusChanged], now, batchSize: 10);

        Assert.Empty(claimed);
    }

    [Fact]
    public void SelectClaimable_EarlierBackingOffRowBlocksLaterRowOfSameApplication()
    {
        var now = DateTime.UtcNow.AddSeconds(1); // rows are due from their creation time
        var applicationId = Guid.NewGuid();
        var submitted = NewRow(applicationId);
        submitted.RecordFailure(now.AddMinutes(5));
        var statusChanged = NewRow(applicationId);

        Assert.Empty(OutboxClaimPolicy.SelectClaimable([submitted, statusChanged], now, batchSize: 10));
    }

    [Fact]
    public void SelectClaimable_RowsOfDifferentApplicationsAreIndependent()
    {
        var now = DateTime.UtcNow.AddSeconds(1); // rows are due from their creation time
        var first = NewRow(Guid.NewGuid());
        first.Lease(now.AddMinutes(2));
        var other = NewRow(Guid.NewGuid());

        Assert.Equal([other], OutboxClaimPolicy.SelectClaimable([first, other], now, batchSize: 10));
    }

    [Fact]
    public void SelectClaimable_ReleasedRowIsClaimableAgain()
    {
        var now = DateTime.UtcNow.AddSeconds(1); // rows are due from their creation time
        var row = NewRow(Guid.NewGuid());
        row.Lease(now.AddMinutes(2));
        row.Release(now);

        Assert.Equal([row], OutboxClaimPolicy.SelectClaimable([row], now, batchSize: 10));
    }

    private static OutboxEvent NewRow(Guid applicationId) =>
        new(ApplicationEventTypes.Submitted, "{}", applicationId);
}
