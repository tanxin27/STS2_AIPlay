using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_AIPlay.Llm;

/// <summary>
/// 死因分析器 - 分析失败原因并生成可执行建议
/// </summary>
public class DeathAnalyzer
{
    /// <summary>
    /// 分析死因
    /// </summary>
    public DeathAnalysis Analyze(
        RunSummary run,
        List<DecisionRecord> decisions,
        List<string> deck,
        List<string> relics)
    {
        var analysis = new DeathAnalysis
        {
            RunId = run.RunId,
            DeathFloor = run.Floor,
            KilledBy = run.KilledBy ?? "Unknown",
            ImmediateCause = DetermineImmediateCause(run.KilledBy),
            Factors = new List<DeathFactor>(),
            KeyMistakes = new List<string>(),
            CriticalDecisions = new List<DecisionRecord>(),
            ActionableAdvice = new List<ActionableAdvice>()
        };
        
        // 1. 分析卡组问题
        AnalyzeDeckIssues(deck, analysis);
        
        // 2. 分析决策错误
        AnalyzeDecisionMistakes(decisions, run.Floor, analysis);
        
        // 3. 分析路线问题
        AnalyzePathIssues(decisions, analysis);
        
        // 4. 生成可执行建议
        GenerateAdvice(analysis);
        
        // 5. 生成总结
        analysis.Summary = GenerateSummary(analysis);
        
        return analysis;
    }
    
    private string DetermineImmediateCause(string? killedBy)
    {
        if (string.IsNullOrEmpty(killedBy))
            return "Unknown";
        
        // 分类敌人
        var eliteEnemies = new[] { "GremlinNob", "Lagavulin", "Sentries", "Book of Stabbing", "Gremlin Leader", "Taskmaster" };
        var bossEnemies = new[] { "Slime Boss", "The Guardian", "Hexaghost", "Bronze Automaton", "The Champ", "The Collector", "Awakened One", "Time Eater", "Donu and Deca" };
        
        if (bossEnemies.Any(b => killedBy.Contains(b)))
            return "Boss Fight";
        if (eliteEnemies.Any(e => killedBy.Contains(e)))
            return "Elite Fight";
        
        return "Regular Combat";
    }
    
    private void AnalyzeDeckIssues(List<string> deck, DeathAnalysis analysis)
    {
        if (deck.Count < 15)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "deck",
                Description = $"Deck too small ({deck.Count} cards), limited options",
                Severity = 0.4f,
                Preventable = false
            });
        }
        else if (deck.Count > 35)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "deck",
                Description = $"Deck too bloated ({deck.Count} cards), inconsistent draws",
                Severity = 0.7f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("Took too many cards without enough removal");
        }
        
        // 检查是否有防御
        var defenseCards = deck.Where(c => 
            c.Contains("Defend") || 
            c.Contains("Backflip") || 
            c.Contains("Dodge") ||
            c.Contains("Blur") ||
            c.Contains("Footwork")).Count();
        
        if (defenseCards < 3 && deck.Count > 15)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "deck",
                Description = "Insufficient defense cards",
                Severity = 0.8f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("Did not prioritize defense cards");
        }
        
        // 检查是否有输出
        var attackCards = deck.Where(c => 
            !c.Contains("Strike") && 
            !c.Contains("Defend") &&
            (c.Contains("Attack") || 
             c.Contains("Damage") ||
             c.Contains("Poison") ||
             c.Contains("Shiv"))).Count();
        
        if (attackCards < 3 && deck.Count > 10)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "deck",
                Description = "Insufficient scaling damage",
                Severity = 0.7f,
                Preventable = true
            });
        }
    }
    
    private void AnalyzeDecisionMistakes(List<DecisionRecord> decisions, int deathFloor, DeathAnalysis analysis)
    {
        // 找死前5层的决策
        var recentDecisions = decisions.Where(d => deathFloor - d.Floor <= 5 && deathFloor - d.Floor >= 0)
            .OrderByDescending(d => d.Floor)
            .ToList();
        
        foreach (var decision in recentDecisions)
        {
            // 检查精英战决策
            if (decision.Type == DecisionType.PathChoice && 
                decision.Chosen.ToLower().Contains("elite"))
            {
                var hpPercent = (float)decision.Context.Hp / decision.Context.MaxHp;
                if (hpPercent < 0.5f)
                {
                    analysis.CriticalDecisions.Add(decision);
                    analysis.KeyMistakes.Add($"Fought elite at {decision.Context.Hp}/{decision.Context.MaxHp} HP ({hpPercent:P0})");
                    analysis.Factors.Add(new DeathFactor
                    {
                        Category = "path",
                        Description = "Took elite fight with low HP",
                        Severity = 0.9f,
                        Preventable = true
                    });
                }
            }
            
            // 检查跳过休息点
            if (decision.Type == DecisionType.PathChoice &&
                decision.Chosen.ToLower().Contains("monster") &&
                deathFloor - decision.Floor <= 2)
            {
                var hpPercent = (float)decision.Context.Hp / decision.Context.MaxHp;
                if (hpPercent < 0.4f)
                {
                    analysis.CriticalDecisions.Add(decision);
                    analysis.KeyMistakes.Add("Skipped rest site with low HP before boss/elite");
                }
            }
            
            // 检查战斗中的药水使用
            if (decision.Type == DecisionType.CombatAction &&
                deathFloor - decision.Floor == 0)
            {
                // 如果死了还有药水，说明没用
                if (decision.Context.Potions.Any())
                {
                    analysis.KeyMistakes.Add("Died with unused potions");
                }
            }
        }
    }
    
    private void AnalyzePathIssues(List<DecisionRecord> decisions, DeathAnalysis analysis)
    {
        // 获取所有路线决策
        var pathDecisions = decisions.Where(d => d.Type == DecisionType.PathChoice)
            .OrderBy(d => d.Floor)
            .ToList();
        
        // 统计本局走的路线
        var eliteFights = pathDecisions.Count(d => d.Chosen.ToLower().Contains("elite"));
        var restSites = pathDecisions.Count(d => d.Chosen.ToLower().Contains("rest"));
        var shopVisits = pathDecisions.Count(d => d.Chosen.ToLower().Contains("shop"));
        
        // 检测连续精英模式
        var consecutiveElites = 0;
        var maxConsecutiveElites = 0;
        var eliteSequence = new List<(int Floor, int Hp, int MaxHp)>();
        
        foreach (var decision in pathDecisions)
        {
            if (decision.Chosen.ToLower().Contains("elite"))
            {
                consecutiveElites++;
                maxConsecutiveElites = Math.Max(maxConsecutiveElites, consecutiveElites);
                eliteSequence.Add((decision.Floor, decision.Context.Hp, decision.Context.MaxHp));
            }
            else if (decision.Chosen.ToLower().Contains("rest"))
            {
                consecutiveElites = 0; // 重置计数
            }
        }
        
        // 检测"精英→精英→死亡"模式
        if (maxConsecutiveElites >= 2)
        {
            var lastElites = eliteSequence.TakeLast(2).ToList();
            var hpDrops = lastElites.Select(e => e.Hp * 100 / e.MaxHp).ToList();
            
            analysis.Factors.Add(new DeathFactor
            {
                Category = "path",
                Description = $"Fought {maxConsecutiveElites} consecutive elites without resting. HP dropped from {string.Join("% → ", hpDrops)}%",
                Severity = 0.9f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("DEATH PATTERN: Consecutive elites without recovery");
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "NEVER fight 2 elites in a row without resting in between",
                TriggerCondition = "Just fought an Elite and considering another Elite",
                Confidence = 0.95f,
                Category = "path"
            });
        }
        
        // 检测"精英后低血量继续战斗"模式
        var deathFloor = analysis.DeathFloor;
        var lastEliteBeforeDeath = eliteSequence.LastOrDefault(e => deathFloor - e.Floor <= 3);
        if (lastEliteBeforeDeath.Floor > 0)
        {
            var hpAfterElite = lastEliteBeforeDeath.Hp * 100 / lastEliteBeforeDeath.MaxHp;
            if (hpAfterElite < 50 && eliteSequence.Count >= 2)
            {
                analysis.Factors.Add(new DeathFactor
                {
                    Category = "path",
                    Description = $"Continued fighting after Elite with only {hpAfterElite}% HP",
                    Severity = 0.85f,
                    Preventable = true
                });
                analysis.KeyMistakes.Add("Fought another battle with low HP after Elite");
            }
        }
        
        // 整体路线评估
        if (eliteFights > 4 && restSites < 2)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "path",
                Description = $"Too many elites ({eliteFights}) with too few rest sites ({restSites})",
                Severity = 0.8f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("Aggressive path without enough recovery");
        }
        
        // 检测"过度战斗"模式 - 只打战斗不去商店/休息
        var monsterFights = pathDecisions.Count(d => d.Chosen.ToLower().Contains("monster"));
        var totalFights = eliteFights + monsterFights;
        var nonFightChoices = pathDecisions.Count - totalFights;
        
        if (totalFights > 6 && shopVisits == 0)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "path",
                Description = $"Fought {totalFights} battles but never visited a Shop. Missed opportunities to remove Strikes and buy potions.",
                Severity = 0.75f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("DEATH PATTERN: Only fought battles, never shopped. Deck likely bloated with Strikes.");
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Visit SHOPS regularly to remove Strikes (~75g) and buy potions",
                TriggerCondition = "Path choice with Shop option and gold > 75",
                Confidence = 0.9f,
                Category = "path"
            });
        }
        
        if (totalFights > 5 && restSites == 0 && analysis.DeathFloor > 10)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "path",
                Description = $"Fought {totalFights} battles without resting. HP likely depleted over time.",
                Severity = 0.7f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("Never rested - HP slowly drained from continuous fights");
        }
        
        // 检测"战斗依赖"模式
        var fightRatio = pathDecisions.Count > 0 ? (float)totalFights / pathDecisions.Count : 0;
        if (fightRatio > 0.8f && pathDecisions.Count >= 5)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "path",
                Description = $"Over-reliance on combat ({fightRatio:P0} fights). Ignored Shops ({shopVisits}) and Events.",
                Severity = 0.65f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("Route too combat-heavy. Need balance of fights, shops, and rests.");
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "BALANCE your route: Fight for cards/gold, Shop for upgrades/removal, Rest for healing",
                TriggerCondition = "Considering path with Shop/Event options after 2+ fights",
                Confidence = 0.85f,
                Category = "path"
            });
        }
        
        // 幕数特定建议
        if (analysis.DeathFloor <= 17 && eliteFights >= 2)
        {
            analysis.Factors.Add(new DeathFactor
            {
                Category = "path",
                Description = "Act 1: Taking multiple elites without building deck first",
                Severity = 0.7f,
                Preventable = true
            });
            analysis.KeyMistakes.Add("Act 1: Too aggressive with elites before deck is ready");
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Act 1: Take max 1 Elite unless HP > 80% and deck is strong",
                TriggerCondition = "Act 1 path choice with Elite option",
                Confidence = 0.8f,
                Category = "path"
            });
        }
    }
    
    private void GenerateAdvice(DeathAnalysis analysis)
    {
        // 根据死因生成具体建议
        
        // 建议1: HP阈值
        var lowHpEliteMistake = analysis.KeyMistakes.FirstOrDefault(m => m.Contains("elite") && m.Contains("HP"));
        if (lowHpEliteMistake != null)
        {
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Avoid elite fights when HP < 50%",
                TriggerCondition = "Considering elite path choice with HP < 50%",
                Confidence = 0.9f,
                Category = "path"
            });
        }
        
        // 建议2: 防御卡
        var defenseIssue = analysis.Factors.FirstOrDefault(f => f.Description.Contains("defense"));
        if (defenseIssue != null)
        {
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Prioritize defense cards in Act 1, aim for at least 4 by Act 2",
                TriggerCondition = "Card reward with defense options in Act 1",
                Confidence = 0.8f,
                Category = "deck_building"
            });
        }
        
        // 建议3: 药水使用
        if (analysis.KeyMistakes.Any(m => m.Contains("potion")))
        {
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Use potions proactively in tough fights, don't save them",
                TriggerCondition = "Combat with < 50% HP and potions available",
                Confidence = 0.85f,
                Category = "combat"
            });
        }
        
        // 建议4: 卡组大小
        var bloatedDeck = analysis.Factors.FirstOrDefault(f => f.Description.Contains("bloated"));
        if (bloatedDeck != null)
        {
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Remove Strikes at shops, keep deck under 25 cards",
                TriggerCondition = "Shop visit with gold > 75",
                Confidence = 0.75f,
                Category = "deck_building"
            });
        }
        
        // 建议5: 休息点
        if (analysis.KeyMistakes.Any(m => m.Contains("rest site")))
        {
            analysis.ActionableAdvice.Add(new ActionableAdvice
            {
                Advice = "Rest when HP < 40%, especially before boss/elite",
                TriggerCondition = "Path choice with rest option and HP < 40%",
                Confidence = 0.9f,
                Category = "path"
            });
        }
    }
    
    private string GenerateSummary(DeathAnalysis analysis)
    {
        var parts = new List<string>();
        
        parts.Add($"Died on floor {analysis.DeathFloor} to {analysis.KilledBy} ({analysis.ImmediateCause})");
        
        if (analysis.KeyMistakes.Any())
        {
            parts.Add($"Key mistakes: {string.Join("; ", analysis.KeyMistakes.Take(2))}");
        }
        
        if (analysis.ActionableAdvice.Any())
        {
            parts.Add($"Top advice: {analysis.ActionableAdvice.First().Advice}");
        }
        
        return string.Join(". ", parts);
    }
    
    /// <summary>
    /// 生成自然语言分析（给LLM看）
    /// </summary>
    public string GenerateAnalysisForPrompt(DeathAnalysis analysis)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("## 死因分析");
        sb.AppendLine();
        sb.AppendLine($"**死亡层数**: {analysis.DeathFloor}");
        sb.AppendLine($"**被谁击杀**: {analysis.KilledBy}");
        sb.AppendLine($"**死因类型**: {analysis.ImmediateCause}");
        sb.AppendLine();
        
        if (analysis.Factors.Any())
        {
            sb.AppendLine("### 影响因素");
            foreach (var factor in analysis.Factors.OrderByDescending(f => f.Severity))
            {
                var preventable = factor.Preventable ? "[可预防] " : "";
                var severity = factor.Severity > 0.7 ? "高" : factor.Severity > 0.4 ? "中" : "低";
                sb.AppendLine($"- {preventable}[{severity}] {factor.Description}");
            }
            sb.AppendLine();
        }
        
        if (analysis.KeyMistakes.Any())
        {
            sb.AppendLine("### 关键错误");
            foreach (var mistake in analysis.KeyMistakes.Take(3))
            {
                sb.AppendLine($"- {mistake}");
            }
            sb.AppendLine();
        }
        
        if (analysis.ActionableAdvice.Any())
        {
            sb.AppendLine("### 改进建议");
            foreach (var advice in analysis.ActionableAdvice)
            {
                sb.AppendLine($"- **{advice.Advice}**");
                sb.AppendLine($"  适用情境: {advice.TriggerCondition} (信心度: {advice.Confidence:P0})");
            }
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}
