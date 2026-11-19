using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Service;

internal sealed record VocabularyPlanStatsResponse(bool Success, IReadOnlyList<VocabularyPlanStats> Data);
