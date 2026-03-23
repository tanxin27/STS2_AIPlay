using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_AIPlay.Llm;

public static class GameStateSerializer
{
    private static string P(string key) => PromptStrings.Get(key);
    private static string P(string key, params object[] args) => PromptStrings.Get(key, args);

    // Track which map we've already shown in full (by row count + boss type as fingerprint)
    private static string? _lastMapFingerprint;

    /// <summary>Reset map tracking (call on new session/game restart).</summary>
    public static void ResetMapTracking() => _lastMapFingerprint = null;

    public static string SerializeCombat(CombatManager cm, string? tacticsHint = null)
    {
        var sb = new StringBuilder();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) return P("CombatUnavailable");

        var creature = player.Creature;
        var pcs = player.PlayerCombatState;

        sb.AppendLine(P("CombatHeader"));
        
        // 添加基于HP的动态策略警告
        var hpPercent = creature.MaxHp > 0 ? (creature.CurrentHp * 100 / creature.MaxHp) : 100;
        var hpWarning = ConservativeStrategy.GetCombatHpWarning(hpPercent);
        if (!string.IsNullOrEmpty(hpWarning))
        {
            sb.AppendLine(hpWarning);
            sb.AppendLine();
        }
        
        // 添加战术提示（如果有）
        if (!string.IsNullOrEmpty(tacticsHint))
        {
            sb.AppendLine(tacticsHint);
            sb.AppendLine();
        }
        
        sb.AppendLine(P("HpBlockEnergy", creature.CurrentHp, creature.MaxHp, creature.Block, pcs?.Energy ?? 0, player.MaxEnergy));

        // Relics
        var relics = player.Relics?.ToList();
        if (relics != null && relics.Count > 0)
        {
            sb.AppendLine(P("YourRelics", string.Join(", ",
                relics.Select(r => FormatRelic(r)))));
        }

        // Powers on player
        var powers = creature.Powers.ToList();
        if (powers.Count > 0)
            sb.AppendLine(P("YourPowers", string.Join(", ", powers.Select(p => FormatPower(p)))));

        // Hand
        sb.AppendLine();
        sb.AppendLine(P("Hand"));
        var hand = PileType.Hand.GetPile(player).Cards.ToList();
        for (int i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            var cost = c.EnergyCost.GetResolved();
            var desc = SafeGetDescription(c);
            var target = c.TargetType == TargetType.AnyEnemy ? P("TargetSingleEnemy") : "";
            var playable = c.CanPlay(out _, out _) ? "" : P("Unplayable");
            sb.AppendLine($"  [{i + 1}] {c.Id.Entry} ({c.Type}, {cost} {P("Energy")}){target}{playable} — {desc}");
        }

        // Draw/Discard counts
        var drawCount = PileType.Draw.GetPile(player).Cards.Count;
        var discardCount = PileType.Discard.GetPile(player).Cards.Count;
        sb.AppendLine(P("DrawDiscardPile", drawCount, discardCount));

        // Potions — log all slots for debugging
        var allPotions = player.Potions.ToList();
        MainFile.Logger.Info($"[AutoSlay/DBG] Potion count={allPotions.Count} slots: {string.Join(", ", allPotions.Select(p => $"{p.Id.Entry}(queued={p.IsQueued},removed={p.HasBeenRemovedFromState})"))}");
        var potions = allPotions.Where(p => !p.HasBeenRemovedFromState).ToList();
        if (potions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(P("Potions"));
            for (int i = 0; i < potions.Count; i++)
            {
                var p = potions[i];
                var desc = StripBBCode(p.DynamicDescription?.GetFormattedText() ?? p.Description?.GetFormattedText() ?? "");
                var targetStr = p.TargetType == TargetType.AnyEnemy ? P("TargetSingleEnemy") : "";
                sb.AppendLine($"  [P{i + 1}] {p.Id.Entry}{targetStr} — {desc}");
            }
        }

        // Enemies
        sb.AppendLine();
        sb.AppendLine(P("Enemies"));
        var combatState = cm.DebugOnlyGetState();
        var enemies = combatState?.Enemies.Where(e => e.IsAlive).ToList() ?? new List<Creature>();
        char letter = 'A';
        foreach (var e in enemies)
        {
            var intentStr = GetIntentString(e);
            var ePowers = e.Powers.ToList();
            var powerStr = ePowers.Count > 0
                ? $" | {P("Powers", string.Join(", ", ePowers.Select(p => FormatPower(p))))}"
                : "";
            sb.AppendLine($"  [{letter}] {e.Monster?.Id.Entry ?? P("Unknown")} — HP: {e.CurrentHp}/{e.MaxHp} | Block: {e.Block}{powerStr}");
            sb.AppendLine($"       {P("Intent", intentStr)}");
            letter++;
        }

        sb.AppendLine();
        sb.AppendLine(P("CombatInstruction"));
        return sb.ToString();
    }

    public static string SerializeCardReward(
        NCardRewardSelectionScreen screen,
        ArchetypeManager? archetypeManager = null,
        string? characterId = null,
        List<string>? currentDeck = null,
        List<string>? currentRelics = null,
        int currentAct = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine(P("CardRewardHeader"));
        
        // ========== 新增：流派分析 ==========
        if (archetypeManager != null && !string.IsNullOrEmpty(characterId) && currentDeck != null)
        {
            try
            {
                AppendArchetypeAnalysis(sb, archetypeManager, characterId, currentDeck, currentRelics ?? new List<string>(), currentAct);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[GameStateSerializer] 流派分析失败: {ex.Message}");
            }
        }
        // ==================================
        
        sb.AppendLine(P("ChooseCardForDeck"));

        var holders = AutoSlayHelpers.FindAll<NCardHolder>(screen);
        
        // ========== 新增：卡牌评估 ==========
        List<(string cardId, CardEvaluation eval)>? cardEvaluations = null;
        if (archetypeManager != null && !string.IsNullOrEmpty(characterId) && currentDeck != null)
        {
            try
            {
                var evaluator = new SmartCardEvaluator(archetypeManager);
                var cardIds = holders.Select(h => h.CardModel?.Id.Entry ?? "Unknown").ToList();
                cardEvaluations = evaluator.EvaluateChoices(cardIds, currentDeck, currentRelics ?? new List<string>(), characterId, currentAct);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[GameStateSerializer] 卡牌评估失败: {ex.Message}");
            }
        }
        // ==================================
        
        for (int i = 0; i < holders.Count; i++)
        {
            var card = holders[i].CardModel;
            if (card != null)
            {
                var desc = SafeGetDescription(card);
                var cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString();
                sb.AppendLine($"  [{i + 1}] {card.Id.Entry} ({card.Type}, {cost} {P("Energy")}, {card.Rarity}) — {desc}");
                
                // ========== 新增：显示评估信息 ==========
                var eval = cardEvaluations?.FirstOrDefault(e => e.cardId == card.Id.Entry).eval;
                if (eval != null)
                {
                    var indicator = eval.TotalScore >= 8f ? "⭐" : eval.TotalScore >= 6f ? "▲" : "○";
                    sb.AppendLine($"      {indicator} [{eval.Recommendation}] {eval.Reasoning}");
                    
                    // 显示流派匹配
                    if (eval.MatchingArchetypes.Any())
                    {
                        sb.AppendLine($"      → 适合流派: {string.Join(", ", eval.MatchingArchetypes.Take(2))}");
                    }
                    
                    // 显示协同
                    if (eval.RelevantSynergies.Any())
                    {
                        foreach (var syn in eval.RelevantSynergies.Take(1))
                        {
                            var otherCards = syn.Cards.Where(c => Normalize(c) != Normalize(card.Id.Entry));
                            sb.AppendLine($"      ⚡ 与你卡组中的 {string.Join(", ", otherCards.Take(2))} 有协同！");
                        }
                    }
                }
                // ==================================
            }
            else
                sb.AppendLine($"  [{i + 1}] {P("UnknownCard")}");
        }

        sb.AppendLine($"  [{holders.Count + 1}] {P("SkipCard")}");
        sb.AppendLine();
        sb.AppendLine(P("ReplyChoose"));
        return sb.ToString();
    }
    
    private static void AppendArchetypeAnalysis(
        StringBuilder sb,
        ArchetypeManager manager,
        string characterId,
        List<string> deck,
        List<string> relics,
        int currentAct)
    {
        var analyzer = new DeckAnalyzer(manager);
        var analysis = analyzer.Analyze(deck, relics, characterId, currentAct);
        
        // 当前卡组概况
        sb.AppendLine("\n【当前卡组概况】");
        sb.AppendLine($"总卡牌: {analysis.TotalCards} | 关键牌: {(analysis.KeyCards.Any() ? string.Join(", ", analysis.KeyCards.Take(3)) : "暂无")}");
        
        // 参考流派分析
        var refMatches = analysis.ArchetypeMatches.Where(m => m.Source == "reference").Take(2).ToList();
        if (refMatches.Any())
        {
            sb.AppendLine("\n【参考流派 - 社区总结的好策略】");
            foreach (var match in refMatches)
            {
                var indicator = match.MatchScore > 60 ? "⭐" : match.MatchScore > 30 ? "◆" : "◇";
                sb.AppendLine($"{indicator} {match.Name}: 匹配度 {match.MatchScore:F0}% ({match.MustHaveOwned}/{match.MustHaveTotal} 核心卡)");
                
                if (match.MissingKeyCards.Any())
                    sb.AppendLine($"   缺失核心: {string.Join(", ", match.MissingKeyCards.Take(2))}");
            }
        }
        
        // AI个人经验
        var personalMatches = analysis.ArchetypeMatches.Where(m => m.Source == "discovered").Take(1).ToList();
        if (personalMatches.Any())
        {
            sb.AppendLine("\n【你的个人经验】");
            foreach (var match in personalMatches)
            {
                sb.AppendLine($"💡 {match.Name}: 相似度 {match.MatchScore:F0}%");
                if (!string.IsNullOrEmpty(match.StrategyHint))
                    sb.AppendLine($"   心得: {match.StrategyHint}");
            }
        }
        
        // 提示
        sb.AppendLine("\n【选牌提示】");
        sb.AppendLine("以上信息是参考建议，你有最终决策权。考虑：当前需要、卡牌质量、长期构建。");
    }

    public static string SerializeRewards(NRewardsScreen screen)
    {
        var sb = new StringBuilder();
        sb.AppendLine(P("RewardsHeader"));
        sb.AppendLine(P("RewardsIntro"));

        var btns = AutoSlayHelpers.FindAll<NRewardButton>(screen);
        int idx = 1;
        bool hasCardReward = false;
        var cardRewards = new List<(int takeIdx, CardReward reward)>();

        for (int i = 0; i < btns.Count; i++)
        {
            if (!btns[i].IsEnabled) continue;
            var reward = btns[i].Reward;
            if (reward == null) continue;

            string desc;
            switch (reward)
            {
                case GoldReward gold:
                    desc = P("GoldReward", gold.Amount);
                    break;
                case CardReward card when card.IsPopulated:
                    cardRewards.Add((idx, card));
                    hasCardReward = true;
                    desc = P("CardRewardDesc");
                    break;
                case PotionReward potion when potion.Potion != null:
                    var potionDesc = StripBBCode(potion.Potion.Description?.GetFormattedText() ?? "");
                    desc = $"{P("PotionReward", potion.Potion.Id.Entry)} — {potionDesc}";
                    break;
                default:
                    desc = StripBBCode(reward.Description?.GetFormattedText() ?? "") ?? reward.GetType().Name;
                    break;
            }
            sb.AppendLine($"  [TAKE {idx}] {desc}");
            idx++;
        }

        // Show card details for all card rewards
        foreach (var (takeIdx, cardReward) in cardRewards)
        {
            sb.AppendLine();
            sb.AppendLine(P("CardChoicesFor", takeIdx));
            int cardIdx = 1;
            foreach (var card in cardReward.Cards)
            {
                var cardDesc = SafeGetDescription(card);
                var cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString();
                sb.AppendLine($"  Card {cardIdx}: {card.Id.Entry} ({card.Type}, {cost} {P("Energy")}, {card.Rarity}) — {cardDesc}");
                cardIdx++;
            }
        }

        sb.AppendLine();
        sb.AppendLine(P("RewardsInstruction"));
        sb.AppendLine(P("TakeInstruction"));
        if (hasCardReward)
            sb.AppendLine(P("CardInstruction"));
        sb.AppendLine(P("DoneInstruction"));
        sb.AppendLine(P("RewardsExample"));
        return sb.ToString();
    }

    public static string SerializeMap(NMapScreen mapScreen, bool includeRouteAnalysis = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(P("MapHeader"));

        var runState = RunManager.Instance?.DebugOnlyGetState();
        
        // Initialize player stats (used throughout the method)
        int currentHp = 0, maxHp = 0, gold = 0;
        ConservativeStrategy.DeckStatus? deckStatus = null;
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player != null)
        {
            currentHp = player.Creature.CurrentHp;
            maxHp = player.Creature.MaxHp;
            gold = player.Gold;
            
            // 获取卡组状态
            var deckCards = player.Deck?.Cards?.Select(c => c.Id.Entry).ToList();
            if (deckCards != null)
            {
                deckStatus = new ConservativeStrategy.DeckStatus
                {
                    CardCount = deckCards.Count,
                    StrikeCount = deckCards.Count(c => c.Contains("Strike")),
                    DefendCount = deckCards.Count(c => c.Contains("Defend"))
                };
            }
        }

        if (runState?.Map != null)
        {
            var map = runState.Map;
            var currentCoord = runState.CurrentMapCoord;

            // Build a fingerprint for this map to detect map changes (3 maps per run)
            var boss = map.BossMapPoint;
            var fingerprint = $"{map.GetRowCount()}_{boss?.PointType}_{boss?.coord.row}";
            bool isNewMap = fingerprint != _lastMapFingerprint;
            if (isNewMap)
                _lastMapFingerprint = fingerprint;

            if (currentCoord.HasValue)
                sb.AppendLine(P("CurrentPosition", currentCoord.Value.row, currentCoord.Value.col));
            else
                sb.AppendLine(P("CurrentPositionStart"));

            // Show player HP and gold
            if (player != null)
            {
                sb.AppendLine(P("HpGold", currentHp, maxHp, gold));
            }

            // Show full map only on first visit or when map changes
            if (isNewMap)
            {
                sb.AppendLine();
                sb.AppendLine(P("FullMap"));

                int rowCount = map.GetRowCount();
                for (int row = 1; row <= rowCount; row++)
                {
                    var rowPoints = map.GetPointsInRow(row).Where(p => p != null).ToList();
                    if (rowPoints.Count == 0) continue;

                    var rowParts = new List<string>();
                    foreach (var p in rowPoints)
                    {
                        var marker = "";
                        if (currentCoord.HasValue && p.coord.row == currentCoord.Value.row && p.coord.col == currentCoord.Value.col)
                            marker = " <<<YOU";
                        var children = p.Children != null && p.Children.Count > 0
                            ? $" -> {string.Join(",", p.Children.Select(c => $"({c.coord.row},{c.coord.col})"))}"
                            : "";
                        rowParts.Add($"({p.coord.row},{p.coord.col})={p.PointType}{marker}{children}");
                    }
                    sb.AppendLine($"  Row {row}: {string.Join("  ", rowParts)}");
                }

                if (boss != null)
                    sb.AppendLine($"  BOSS: ({boss.coord.row},{boss.coord.col})={boss.PointType}");

                sb.AppendLine();
            }
            
            // Add current route history for context
            var recentChoices = RouteHistoryLogger.GetRecentChoices(3);
            if (recentChoices.Count > 0)
            {
                sb.AppendLine("=== YOUR ROUTE HISTORY (This Run) ===");
                foreach (var choice in recentChoices)
                {
                    var outcome = string.IsNullOrEmpty(choice.Outcome) ? "" : 
                        $" → {choice.Outcome} ({choice.HpDelta:+#;-#;0} HP)";
                    sb.AppendLine($"Row {choice.ToRow}: {choice.RoomType} | HP: {choice.CurrentHp}/{choice.MaxHp}{outcome}");
                }
                
                // 添加连续路线风险评估
                var riskAssessment = RouteHistoryLogger.GetContinuousRiskAssessment();
                if (!string.IsNullOrEmpty(riskAssessment))
                {
                    sb.AppendLine();
                    sb.AppendLine(riskAssessment);
                }
                sb.AppendLine();
            }
            
            // Add dynamic conservative strategy analysis
            if (includeRouteAnalysis && currentCoord.HasValue && player != null)
            {
                var hpPercent = currentHp * 100 / maxHp;
                var bossRow = boss?.coord.row ?? 17;
                
                // 计算到下一个休息点的距离
                int restDistance = -1;
                for (int row = currentCoord.Value.row + 1; row <= bossRow; row++)
                {
                    var rowPoints = map.GetPointsInRow(row);
                    if (rowPoints.Any(p => p != null && p.PointType.ToString() == "Rest"))
                    {
                        restDistance = row - currentCoord.Value.row;
                        break;
                    }
                }
                
                // 计算连续战斗风险
                var consecutiveElites = recentChoices.Count(c => c.RoomType == "Elite");
                var lastWasElite = recentChoices.LastOrDefault()?.RoomType == "Elite";
                
                var actNumber = 1; // 暂时硬编码，可以后续从其他方式获取
                
                // 使用 ConservativeStrategy 生成动态策略（基于当前状态，不预设规则）
                var strategy = ConservativeStrategy.GetMapStrategy(
                    hpPercent, restDistance, actNumber, 
                    consecutiveElites, lastWasElite,
                    deckStatus, gold);
                sb.AppendLine(strategy);
                sb.AppendLine();
            }
        }

        // Available choices
        sb.AppendLine(P("AvailableNextRooms"));
        var points = AutoSlayHelpers.FindAll<NMapPoint>(mapScreen)
            .Where(p => p.IsEnabled)
            .ToList();

        // 获取路线统计用于房间推荐
        var routeStats = RouteHistoryLogger.GetRouteStats();
        var recentChoicesForStats = RouteHistoryLogger.GetRecentChoices(5);
        var consecutiveFightsTotal = routeStats.CurrentConsecutiveFights + routeStats.CurrentConsecutiveElites;
        var consecutiveElitesTotal = routeStats.CurrentConsecutiveElites;
        var totalFights = routeStats.EliteCount + routeStats.MonsterCount;
        
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var pointType = p.Point?.PointType.ToString() ?? P("Unknown");
            var hpPercent = maxHp > 0 ? (currentHp * 100 / maxHp) : 100;
            var recommendation = GetRoomRecommendation(pointType, hpPercent, consecutiveFightsTotal, consecutiveElitesTotal, totalFights);
            sb.AppendLine($"  [{i + 1}] {pointType} (row {p.Point?.coord.row}, col {p.Point?.coord.col}){recommendation}");
        }

        sb.AppendLine();
        sb.AppendLine(P("ReplyChoose"));
        return sb.ToString();
    }
    
    private static string GetRoomRecommendation(string roomType, int hpPercent, int consecutiveFights, int consecutiveElites, int totalFights)
    {
        // 使用 ConservativeStrategy 动态生成房间推荐（包含连续战斗检测）
        return ConservativeStrategy.GetRoomRiskLabel(roomType, hpPercent, consecutiveFights, consecutiveElites, totalFights);
    }

    public static string SerializeEvent(Node eventRoom)
    {
        var sb = new StringBuilder();
        sb.AppendLine(P("EventHeader"));

        var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
            .Where(o => !o.Option.IsLocked)
            .ToList();

        if (options.Count == 0)
        {
            sb.AppendLine(P("EventNoOptions"));
            return sb.ToString();
        }

        sb.AppendLine(P("ChooseOption"));
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i].Option;
            if (opt.IsProceed)
            {
                sb.AppendLine($"  [{i + 1}] {P("ProceedLeave")}");
            }
            else
            {
                var title = StripBBCode(opt.Title?.GetFormattedText() ?? "");
                var desc = StripBBCode(opt.Description?.GetFormattedText() ?? "");
                var text = string.IsNullOrEmpty(title) ? desc : $"{title}: {desc}";
                if (string.IsNullOrEmpty(text)) text = P("Option");
                sb.AppendLine($"  [{i + 1}] {text}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(P("ReplyChoose"));
        return sb.ToString();
    }

    public static string SerializeRestSite(NRestSiteRoom room)
    {
        var sb = new StringBuilder();
        sb.AppendLine(P("RestSiteHeader"));

        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player != null)
            sb.AppendLine($"HP: {player.Creature.CurrentHp}/{player.Creature.MaxHp}");

        var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(room)
            .Where(b => b.Option.IsEnabled)
            .ToList();

        sb.AppendLine(P("AvailableOptions"));
        for (int i = 0; i < btns.Count; i++)
            sb.AppendLine($"  [{i + 1}] {btns[i].Option.GetType().Name}");

        sb.AppendLine();
        sb.AppendLine(P("ReplyChoose"));
        return sb.ToString();
    }

    public static string SerializeShop(NMerchantRoom room)
    {
        var sb = new StringBuilder();
        sb.AppendLine(P("ShopHeader"));

        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player != null)
            sb.AppendLine(P("GoldHp", player.Gold, player.Creature.CurrentHp, player.Creature.MaxHp));

        var inv = room.Inventory?.Inventory;
        if (inv == null)
        {
            sb.AppendLine(P("ShopUnavailable"));
            return sb.ToString();
        }

        int idx = 1;

        // Cards
        sb.AppendLine();
        sb.AppendLine(P("Cards"));
        foreach (var entry in inv.CardEntries)
        {
            if (!entry.IsStocked) continue;
            var card = entry.CreationResult?.Card;
            if (card == null) continue;
            var desc = SafeGetDescription(card);
            var sale = entry.IsOnSale ? P("Sale") : "";
            var affordable = entry.EnoughGold ? "" : P("NotEnoughGold");
            sb.AppendLine($"  [{idx}] {card.Id.Entry} ({card.Type}, {card.EnergyCost.Canonical} {P("Energy")}, {card.Rarity}) — {desc} | {P("Cost", entry.Cost)}{sale}{affordable}");
            idx++;
        }

        // Relics
        sb.AppendLine();
        sb.AppendLine(P("Relics"));
        foreach (var entry in inv.RelicEntries)
        {
            if (!entry.IsStocked) continue;
            var relic = entry.Model;
            if (relic == null) continue;
            var desc = StripBBCode(relic.Description?.GetFormattedText() ?? "");
            var affordable = entry.EnoughGold ? "" : P("NotEnoughGold");
            sb.AppendLine($"  [{idx}] {relic.Id.Entry} ({relic.Rarity}) — {desc} | {P("Cost", entry.Cost)}{affordable}");
            idx++;
        }

        // Potions
        sb.AppendLine();
        sb.AppendLine(P("Potions"));
        foreach (var entry in inv.PotionEntries)
        {
            if (!entry.IsStocked) continue;
            var potion = entry.Model;
            if (potion == null) continue;
            var desc = StripBBCode(potion.Description?.GetFormattedText() ?? "");
            var affordable = entry.EnoughGold ? "" : P("NotEnoughGold");
            sb.AppendLine($"  [{idx}] {potion.Id.Entry} ({potion.Rarity}) — {desc} | {P("Cost", entry.Cost)}{affordable}");
            idx++;
        }

        // Card Removal
        if (inv.CardRemovalEntry != null && inv.CardRemovalEntry.IsStocked)
        {
            sb.AppendLine();
            var affordable = inv.CardRemovalEntry.EnoughGold ? "" : P("NotEnoughGold");
            sb.AppendLine($"  [{idx}] {P("RemoveCard")} | {P("Cost", inv.CardRemovalEntry.Cost)}{affordable}");
            idx++;
        }

        sb.AppendLine();
        sb.AppendLine($"  [{idx}] {P("LeaveShop")}");
        sb.AppendLine();
        sb.AppendLine(P("ShopInstruction"));
        sb.AppendLine("Example:");
        sb.AppendLine("  BUY 3");
        sb.AppendLine("  BUY 7");
        sb.AppendLine("  LEAVE");
        return sb.ToString();
    }

    public static string ReadScreenLabel(Node screen)
    {
        try
        {
            var label = screen.GetNodeOrNull<Godot.RichTextLabel>("%BottomLabel");
            if (label != null)
                return StripBBCode(label.Text ?? "");
        }
        catch { /* ignore */ }
        return "";
    }

    public static string SerializeCardGrid(Node screen, string screenType)
    {
        var sb = new StringBuilder();
        // Try to read the actual screen prompt (e.g. "选择2张牌来移除。")
        var screenLabel = ReadScreenLabel(screen);
        if (!string.IsNullOrEmpty(screenLabel))
            sb.AppendLine($"=== {StripBBCode(screenLabel)} ===");
        else
            sb.AppendLine($"=== {screenType} ===");
        sb.AppendLine(P("ChooseCardFromDeck"));

        var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(screen);
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i].CardModel;
            if (card != null)
            {
                var desc = SafeGetDescription(card);
                var cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString();
                sb.AppendLine($"  [{i + 1}] {card.Id.Entry} ({card.Type}, {cost} {P("Energy")}) — {desc}");
            }
            else
                sb.AppendLine($"  [{i + 1}] {P("UnknownCard")}");
        }

        sb.AppendLine();
        sb.AppendLine(P("ReplyChoose"));
        return sb.ToString();
    }

    public static string SerializeGenericChoice(Node screen, string title, int optionCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {title} ===");
        sb.AppendLine(P("GenericChoice", optionCount, optionCount));
        sb.AppendLine(P("ReplyChoose"));
        return sb.ToString();
    }

    public static string SafeGetCardDescription(CardModel card) => SafeGetDescription(card);

    private static string SafeGetDescription(CardModel card)
    {
        string raw;
        try { raw = card.GetDescriptionForPile(PileType.Hand) ?? ""; }
        catch
        {
            try { raw = card.Description?.GetFormattedText() ?? ""; }
            catch { return ""; }
        }
        return StripBBCode(raw);
    }

    private static string StripBBCode(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Replace energy icon images with text: consecutive icons → count + "energy"
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(\[img\]res://images/packed/sprite_fonts/\w+_energy_icon\.png\[/img\])+",
            m => {
                int count = System.Text.RegularExpressions.Regex.Matches(m.Value, @"\[img\]").Count;
                return $"{count}{P("Energy")}";
            });
        // Remove remaining [img]...[/img] entirely
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[img\].*?\[/img\]", "");
        // Remove [tag] and [/tag] but keep inner text
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[^\]]+\]", "");
        return text.Trim();
    }

    private static string FormatRelic(RelicModel r)
    {
        try
        {
            var desc = StripBBCode(r.DynamicDescription?.GetFormattedText() ?? "");
            if (!string.IsNullOrEmpty(desc))
                return $"{r.Id.Entry}({desc})";
        }
        catch { /* ignore */ }
        return r.Id.Entry;
    }

    private static string FormatPower(PowerModel p)
    {
        try
        {
            var desc = StripBBCode(p.DumbHoverTip.Description);
            if (!string.IsNullOrEmpty(desc))
                return $"{p.GetType().Name}({p.Amount}: {desc})";
        }
        catch { /* ignore */ }
        return $"{p.GetType().Name}({p.Amount})";
    }

    private static string GetIntentString(Creature enemy)
    {
        var move = enemy.Monster?.NextMove;
        if (move == null) return P("Unknown");

        var intents = move.Intents;
        if (intents == null || intents.Count == 0) return P("Unknown");

        var parts = new List<string>();
        foreach (var intent in intents)
        {
            if (intent is AttackIntent attack)
            {
                try
                {
                    var targets = enemy.CombatState?.PlayerCreatures ?? new List<Creature>();
                    var dmg = attack.GetTotalDamage(targets, enemy);
                    parts.Add(P("Attack", dmg));
                }
                catch
                {
                    parts.Add(P("AttackUnknown"));
                }
            }
            else
            {
                parts.Add(intent.IntentType.ToString());
            }
        }
        return string.Join(" + ", parts);
    }
    
    private static string Normalize(string id) => 
        id.ToLowerInvariant().Replace(" ", "").Replace("'", "").Replace("-", "");
}
