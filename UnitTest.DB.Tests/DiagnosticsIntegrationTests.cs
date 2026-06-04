using System.Diagnostics;
using System.Diagnostics.Metrics;
using Socigy.OpenSource.DB.Core.Diagnostics;

namespace UnitTest.DB.Tests;

/// <summary>
/// Live-PostgreSQL tests that the library emits OpenTelemetry activities and metrics for executed SQL.
/// </summary>
[TestFixture]
public class DiagnosticsIntegrationTests : BaseUnitTest
{
    [SetUp]
    public Task CleanDiag() => ClearAsync("test_items");

    [Test]
    public async Task Insert_EmitsActivity_WithDbSemanticTags()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SocigyDbInstrumentation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        await new TestItem { Id = Guid.NewGuid(), Name = "diag" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        var insert = activities.FirstOrDefault(a => "INSERT".Equals(a.GetTagItem("db.operation.name")));
        Assert.That(insert, Is.Not.Null, "expected an INSERT activity");
        Assert.That(insert!.GetTagItem("db.system"), Is.EqualTo("postgresql"));
        Assert.That(insert.GetTagItem("db.query.text"), Is.Not.Null);
    }

    [Test]
    public async Task Query_RecordsDurationHistogram()
    {
        bool recorded = false;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SocigyDbInstrumentation.MeterName &&
                    instrument.Name == "db.client.operation.duration")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => recorded = true);
        meterListener.Start();

        await new TestItem { Id = Guid.NewGuid(), Name = "metric" }
            .Insert().WithConnection(Connection).ExecuteAsync();

        await foreach (var _ in TestItem.Query().WithConnection(Connection).ExecuteAsync()) { }

        Assert.That(recorded, Is.True, "expected the duration histogram to record at least one measurement");
    }
}
