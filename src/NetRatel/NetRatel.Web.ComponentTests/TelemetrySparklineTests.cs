using Bunit;
using FluentAssertions;
using NetRatel.Web.Components.Dialogs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TelemetrySparklineTests : AsyncBunitContext
{
    [Fact]
    public void Renders_OneSeries_WithAccessibleTitleAndDescription()
    {
        var cut = Render<TelemetrySparkline>(parameters => parameters
            .Add(component => component.Label, "CPU utilisation")
            .Add(component => component.Values, new[] { 0d, 50d, 100d })
            .Add(component => component.MinimumValue, 0d)
            .Add(component => component.MaximumValue, 100d));

        cut.FindAll("polyline").Should().HaveCount(1);
        cut.Find("title").TextContent.Should().Contain("CPU utilisation telemetry history");
        cut.Find("desc").TextContent.Should().Contain("one series");
        cut.Find("polyline").GetAttribute("points").Should().Be("0.00,30.00 50.00,16.00 100.00,2.00");
    }

    [Fact]
    public void Renders_TwoSeries_WithSharedGeometry()
    {
        var cut = Render<TelemetrySparkline>(parameters => parameters
            .Add(component => component.Label, "Network")
            .Add(component => component.Values, new[] { 0d, 100d })
            .Add(component => component.SecondaryValues, new[] { 100d, 0d })
            .Add(component => component.MinimumValue, 0d)
            .Add(component => component.MaximumValue, 100d));

        cut.FindAll("polyline").Should().HaveCount(2);
        cut.Find("desc").TextContent.Should().Contain("primary and secondary series");
        cut.FindAll("polyline")[1].GetAttribute("points").Should().Be("0.00,2.00 100.00,30.00");
    }

    [Fact]
    public void Renders_FlatHistory_CentredWithoutDivisionErrors()
    {
        var cut = Render<TelemetrySparkline>(parameters => parameters
            .Add(component => component.Values, new[] { 42d, 42d, 42d }));

        cut.Find("polyline").GetAttribute("points").Should().Be("0.00,16.00 50.00,16.00 100.00,16.00");
    }

    [Fact]
    public void Renders_EmptyHistory_State()
    {
        var cut = Render<TelemetrySparkline>(parameters => parameters
            .Add(component => component.Values, Array.Empty<double>()));

        cut.Find(".telemetry-sparkline-empty").TextContent.Should().Contain("Awaiting history");
        cut.FindAll("polyline").Should().BeEmpty();
    }
}
