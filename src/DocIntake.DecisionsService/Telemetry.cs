using System.Diagnostics;
using DocIntake.ServiceDefaults;

namespace DocIntake.DecisionsService;

/// <summary>The service's own spans (registered with the tracer in <see cref="IntakeTelemetry"/>).</summary>
internal static class Telemetry
{
    public static ActivitySource Source => IntakeTelemetry.Source;
}
