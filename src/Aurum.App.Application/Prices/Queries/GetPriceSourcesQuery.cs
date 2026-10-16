using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Prices.DTOs;

namespace Aurum.App.Application.Prices.Queries;

/// <summary>Every configured price source with its health and budget, in priority order.</summary>
/// <remarks>No validator: there is no input.</remarks>
public sealed record GetPriceSourcesQuery : IQuery<IReadOnlyList<PriceSourceStatusDto>>;
