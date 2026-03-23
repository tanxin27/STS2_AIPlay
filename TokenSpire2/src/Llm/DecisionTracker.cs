using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TokenSpire2.Llm;

/// <summary>
/// 决策追踪器 - 记录关键决策及其结果
/// </summary>
public class DecisionTracker
{
    private readonly string _decisionsDir;
    private List<DecisionRecord> _currentRunDecisions = new();
    private string? _currentRunId;
    
    public DecisionTracker(string? baseDir = null)
    {
        _decisionsDir = baseDir ?? Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "decisions");
        Directory.CreateDirectory(_decisionsDir);
    }
    
    /// <summary>
    /// 开始新的一局
    /// </summary>
    public void StartNewRun(string runId, string character)
    {
        _currentRunId = runId;
        _currentRunDecisions.Clear();
        MainFile.Logger.Info($"[DecisionTracker] 开始记录对局: {runId}, 角色: {character}");
    }
    
    /// <summary>
    /// 记录一个决策
    /// </summary>
    public DecisionRecord RecordDecision(
        DecisionType type,
        List<string> options,
        string chosen,
        string llmReasoning,
        GameSnapshot context,
        string character)
    {
        if (_currentRunId == null)
        {
            StartNewRun(Guid.NewGuid().ToString(), character);
        }
        
        var decision = new DecisionRecord
        {
            Type = type,
            Floor = context.Hp > 0 ? EstimateFloor(context) : 1, // 简化估算
            Act = EstimateAct(context),
            Character = character,
            Options = options,
            Chosen = chosen,
            LlmReasoning = llmReasoning,
            Context = context
        };
        
        _currentRunDecisions.Add(decision);
        MainFile.Logger.Info($"[DecisionTracker] 记录决策: {type} - 选择了 {chosen} (Floor {decision.Floor})");
        
        return decision;
    }
    
    /// <summary>
    /// 简化版决策记录（不需要完整情境）
    /// </summary>
    public DecisionRecord RecordSimpleDecision(
        DecisionType type,
        int floor,
        List<string> options,
        string chosen,
        string llmReasoning,
        string character)
    {
        var snapshot = new GameSnapshot(); // 空的快照，后续填充
        return RecordDecision(type, options, chosen, llmReasoning, snapshot, character);
    }
    
    /// <summary>
    /// 更新决策结果（游戏结束时调用）
    /// </summary>
    public void UpdateDecisionOutcomes(bool victory, int finalFloor, string? summary = null)
    {
        foreach (var decision in _currentRunDecisions.Where(d => d.Outcome == null))
        {
            decision.Outcome = new DecisionOutcome
            {
                FloorReached = finalFloor,
                EventuallyWon = victory,
                OutcomeType = DetermineOutcomeType(decision, victory, finalFloor),
                Analysis = summary,
                AnalyzedAt = DateTime.Now
            };
            
            // 简单启发：如果决策后不久死亡，可能是坏决策
            if (!victory && finalFloor - decision.Floor <= 3)
            {
                decision.Outcome.WasGoodDecision = false;
            }
            else if (victory && finalFloor >= 50)
            {
                decision.Outcome.WasGoodDecision = true;
            }
        }
        
        SaveCurrentRunDecisions();
    }
    
    /// <summary>
    /// 获取当前对局的所有决策
    /// </summary>
    public List<DecisionRecord> GetCurrentRunDecisions()
    {
        return _currentRunDecisions.ToList();
    }
    
    /// <summary>
    /// 获取特定类型的决策
    /// </summary>
    public List<DecisionRecord> GetDecisionsByType(DecisionType type)
    {
        return _currentRunDecisions.Where(d => d.Type == type).ToList();
    }
    
    /// <summary>
    /// 分析关键决策（可能导致死亡的错误决策）
    /// </summary>
    public List<DecisionRecord> IdentifyCriticalMistakes(int deathFloor)
    {
        var mistakes = new List<DecisionRecord>();
        
        // 距离死亡3层内的决策
        var recentDecisions = _currentRunDecisions
            .Where(d => deathFloor - d.Floor <= 3 && deathFloor - d.Floor >= 0)
            .OrderByDescending(d => d.Floor);
        
        foreach (var decision in recentDecisions)
        {
            // 启发式判断：某些决策类型更容易导致死亡
            if (decision.Type == DecisionType.PathChoice && 
                decision.Chosen.ToLower().Contains("elite") &&
                decision.Context.Hp < decision.Context.MaxHp * 0.5f)
            {
                mistakes.Add(decision);
            }
            
            if (decision.Type == DecisionType.CardPick &&
                deathFloor - decision.Floor <= 1)
            {
                // 死亡前拿的牌可能没有即时帮助
                mistakes.Add(decision);
            }
        }
        
        return mistakes;
    }
    
    /// <summary>
    /// 获取最近10局的决策历史（用于分析模式）
    /// </summary>
    public List<List<DecisionRecord>> GetRecentRunsHistory(int count = 10)
    {
        var runs = new List<List<DecisionRecord>>();
        
        try
        {
            var files = Directory.GetFiles(_decisionsDir, "decisions_*.json")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(count);
            
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var decisions = JsonSerializer.Deserialize<List<DecisionRecord>>(json);
                    if (decisions != null && decisions.Any())
                        runs.Add(decisions);
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[DecisionTracker] 读取历史失败: {ex.Message}");
        }
        
        return runs;
    }
    
    /// <summary>
    /// 生成决策分析摘要（给LLM看）
    /// </summary>
    public string GenerateDecisionAnalysisForPrompt(int topN = 5)
    {
        var sb = new System.Text.StringBuilder();
        
        // 按类型分组统计
        var byType = _currentRunDecisions.GroupBy(d => d.Type).ToList();
        
        sb.AppendLine("### 本局关键决策统计");
        foreach (var group in byType)
        {
            var total = group.Count();
            var withOutcome = group.Count(d => d.Outcome != null);
            var good = group.Count(d => d.Outcome?.WasGoodDecision == true);
            var bad = group.Count(d => d.Outcome?.WasGoodDecision == false);
            
            sb.AppendLine($"- {group.Key}: {total}次 (好: {good}, 坏: {bad}, 待定: {withOutcome - good - bad})");
        }
        
        // 列出具体决策
        sb.AppendLine("\n### 关键决策详情");
        foreach (var decision in _currentRunDecisions.OrderByDescending(d => d.Floor).Take(topN))
        {
            var outcome = decision.Outcome != null 
                ? $"→ 结果: {(decision.Outcome.WasGoodDecision == true ? "✓" : decision.Outcome.WasGoodDecision == false ? "✗" : "?")}, 到达{decision.Outcome.FloorReached}层" 
                : "→ 结果: 待分析";
            
            sb.AppendLine($"Floor {decision.Floor}: [{decision.Type}] 选择 '{decision.Chosen}' {outcome}");
            if (!string.IsNullOrEmpty(decision.LlmReasoning))
                sb.AppendLine($"  推理: {decision.LlmReasoning.Split('\n').First()}...");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 保存当前对局的决策
    /// </summary>
    private void SaveCurrentRunDecisions()
    {
        if (_currentRunId == null || !_currentRunDecisions.Any()) return;
        
        try
        {
            var path = Path.Combine(_decisionsDir, $"decisions_{_currentRunId}.json");
            var json = JsonSerializer.Serialize(_currentRunDecisions, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
            MainFile.Logger.Info($"[DecisionTracker] 保存 {_currentRunDecisions.Count} 个决策到 {path}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[DecisionTracker] 保存决策失败: {ex.Message}");
        }
    }
    
    // ==================== 辅助方法 ====================
    
    private int EstimateFloor(GameSnapshot context)
    {
        // 简化估算，实际应该从游戏状态获取
        return 1;
    }
    
    private int EstimateAct(GameSnapshot context)
    {
        // 简化估算
        return 1;
    }
    
    private string DetermineOutcomeType(DecisionRecord decision, bool victory, int finalFloor)
    {
        if (victory && finalFloor >= 50)
            return "led_to_victory";
        
        if (!victory && finalFloor - decision.Floor <= 5)
            return "led_to_death";
        
        return "neutral";
    }
    
    public void ClearCurrentRun()
    {
        SaveCurrentRunDecisions();
        _currentRunDecisions.Clear();
        _currentRunId = null;
    }
}
