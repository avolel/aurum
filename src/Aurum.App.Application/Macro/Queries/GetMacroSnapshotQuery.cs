using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Macro.DTOs;

namespace Aurum.App.Application.Macro.Queries;

/// <summary>The newest figure of each series that was known at <c>AsOf</c>, in <c>FredSeries.All</c> order.</summary>
/// <param name="AsOf">Null means now, by the injected clock. Past times answer "what was known then" (D-27).</param>
public sealed record GetMacroSnapshotQuery(DateTimeOffset? AsOf) : IQuery<IReadOnlyList<MacroFigureDto>>;
