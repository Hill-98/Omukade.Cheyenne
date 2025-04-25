using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MatchLogic;

namespace Omukade.Cheyenne.Patching
{
    internal static class CardSourcePatchs
    {
        // Token: 0x06000090 RID: 144 RVA: 0x00005D84 File Offset: 0x00003F84
        public static void Patch(CardSource card)
        {
            if (card == null)
            {
                return;
            }
            if (card.cardArchetypeID == "-9367206")
            {
                Trainer_Item_TagCall(card);
            }
            if (card.cardArchetypeID == "-1351516025")
            {
                Trainer_Supporter_GuzmaHala(card);
            }
            if (card.cardArchetypeID == "-906671920")
            {
                Pokemon_Zoroark_PhantomTransformation(card);
            }
        }
        private static SubAction[] GetMatchSubActions(CardSource card)
        {
            MatchAction[] matchActions = new List<MatchAction>().Concat(card.triggeredActions).Concat(card.useActions).ToArray();
            List<SubAction> subActions = new List<SubAction>();
            foreach (MatchAction matchAction in matchActions)
            {
                subActions.AddRange(matchAction.subActions);
            }
            return subActions.ToArray();
        }

        private static void Common_MoveCards_EnableReveal(CardSource card)
        {
            SubAction[] actions = GetMatchSubActions(card);
            foreach (SubAction action in actions)
            {
                if (action is MoveCards)
                {
                    MoveCards moveCards = (MoveCards)action;
                    moveCards.revealCardToOpponent = true;
                    moveCards.revealCardToOwner = true;
                }
            }
        }

        private static void Trainer_Item_TagCall(CardSource card)
        {
            Common_MoveCards_EnableReveal(card);
        }

        private static void Trainer_Supporter_GuzmaHala(CardSource card)
        {
            Common_MoveCards_EnableReveal(card);
        }

        private static void Pokemon_Zoroark_PhantomTransformation(CardSource card)
        {
            foreach (CardAction action in card.useActions)
            {
                if (action.actionName == "[Ability] Phantom Transformation")
                {
                    ExistenceConditional conditional = (ExistenceConditional)action.conditions[0];
                    foreach (EntityFilter filter in conditional.entity.groupFilters)
                    {
                        if (filter is CardFilter)
                        {
                            CardFilter cardFilter = (CardFilter)filter;
                            if (cardFilter.stringVariableTotal is SingleStringTotal)
                            {
                                SingleStringTotal singleString = (SingleStringTotal)cardFilter.stringVariableTotal;
                                if (singleString.stringVariable is ExplicitStringVariable)
                                {
                                    ExplicitStringVariable explicitString = (ExplicitStringVariable)singleString.stringVariable;
                                    if (explicitString.explicitValue == "Zoroark")
                                    {
                                        cardFilter.stringConditionalType = StringConditional.ConditionalType.DoesNotEqual;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
