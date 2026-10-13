using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Prices.DTOs;

namespace Aurum.App.Application.Prices.Queries;

/// <summary>The held price for one symbol. Null when the app has none yet.</summary>
public sealed record GetLivePriceQuery(string Symbol) : IQuery<LivePriceDto?>;
