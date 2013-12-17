﻿namespace Microsoft.Formula.Common.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using System.Text;
    using System.Threading;

    using API;
    using API.ASTQueries;
    using API.Nodes;
    using Compiler;
    using Extras;
    using Terms;

    internal class Executer
    {
        private static char PatternVarBoundPrefix = '^';
        private static char PatternVarUnboundPrefix = '*';

        private ExecuterStatistics stats;
        
        /// <summary>
        /// Maps a renaming to the fact set under that renaming.
        /// </summary>
        private Map<string, FactSet> factSets;

        /// <summary>
        /// Maps a type term to a set of patterns that cover the triggering of that type.
        /// </summary>
        private Map<Term, Set<Term>> typesToTriggersMap = new Map<Term, Set<Term>>(Term.Compare);

        /// <summary>
        /// Maps a find pattern to a subindex.
        /// </summary>
        private Map<Term, SubIndex> trigIndices = new Map<Term, SubIndex>(Term.Compare);

        /// <summary>
        /// Maps a comprehension symbol to a subindex.
        /// </summary>
        private Map<Symbol, SubIndex> comprIndices = new Map<Symbol, SubIndex>(Symbol.Compare);

        /// <summary>
        /// Maps a symbol to a set of indices with patterns beginning with this symbol. 
        /// </summary>
        private Map<Symbol, LinkedList<SubIndex>> symbToIndexMap = new Map<Symbol, LinkedList<SubIndex>>(Symbol.Compare);

        /// <summary>
        /// A map from strata to rules that are not triggered.
        /// </summary>
        private Map<int, LinkedList<CoreRule>> untrigRules =
            new Map<int, LinkedList<CoreRule>>((x, y) => x - y);

        /// <summary>
        /// Maps all facts to their data.
        /// </summary>
        private Map<Term, Set<Derivation>> facts = new Map<Term, Set<Derivation>>(Term.Compare);

        /// <summary>
        /// Indicates if the index is storing derivations
        /// </summary>
        public bool KeepDerivations
        {
            get;
            private set;
        }

        public TermIndex TermIndex
        {
            get;
            private set;
        }

        public RuleTable Rules
        {
            get;
            private set;
        }

        /// <summary>
        /// Contains the fixpoint if executer terminated without cancellation.
        /// </summary>
        public Map<Term, Set<Derivation>> Fixpoint
        {
            get { return facts; }
        }

        /// <summary>
        /// Used for querying a model.
        /// </summary>
        public Executer(
            FactSet factSet, 
            ExecuterStatistics stats,
            bool keepDervs)
        {
            Contract.Requires(factSet != null);
            KeepDerivations = keepDervs;
            TermIndex = factSet.Index;
            Rules = factSet.Rules;
            factSets = new Map<string, FactSet>(string.Compare);
            factSets.Add(string.Empty, factSet);
            this.stats = stats;
            InitializeExecuter(factSet.GetSymbCnstValue);
        }

        /// <summary>
        /// Used for executing a transformation.
        /// </summary>
        public Executer(
            RuleTable rules,
            Map<string, FactSet> modelInputs,
            Map<string, Term> valueInputs,
            ExecuterStatistics stats,
            bool keepDervs)
        {
            Contract.Requires(rules != null && rules.ModuleData.Reduced.Node.NodeKind == NodeKind.Transform);
            Contract.Requires(modelInputs != null);
            Contract.Requires(valueInputs != null);

            KeepDerivations = keepDervs;
            TermIndex = rules.Index;
            Rules = rules;
            factSets = modelInputs;
            this.stats = stats;
            var name = ((Transform)rules.ModuleData.Reduced.Node).Name;

            bool wasAdded;
            UserSymbol other;
            var paramVals = new Map<Term, Term>(Term.Compare);
            foreach (var kv in valueInputs)
            {
                paramVals.Add(
                    TermIndex.MkApply(
                        TermIndex.SymbolTable.Resolve(string.Format("{0}.%{1}", name, kv.Key), out other),
                        TermIndex.EmptyArgs,
                        out wasAdded),
                    kv.Value);
            }

            InitializeExecuter(x => paramVals[x], paramVals.Keys);
        }
       
        /// <summary>
        /// Returns true if the ground term t has been derived.
        /// </summary>
        public bool IsDerived(Term t)
        {
            Contract.Requires(t != null && t.Groundness == Groundness.Ground);
            return facts.ContainsKey(t);
        }

        public void Execute()
        {
            PendingActivation act;
            var pendingAct = new Set<PendingActivation>(PendingActivation.Compare);
            var pendingFacts = new Map<Term, Set<Derivation>>(Term.Compare);
            LinkedList<CoreRule> untrigList;
            for (int i = 0; i < Rules.StratificationDepth; ++i)
            {
                //// At each stratum all rules must be pended for all possible bindings.
                if (untrigRules.TryFindValue(i, out untrigList))
                {
                    foreach (var r in untrigList)
                    {
                        pendingAct.Add(new PendingActivation(r, -1, TermIndex.FalseValue));
                    }
                }

                foreach (var kv in trigIndices)
                {
                    kv.Value.PendAll(pendingAct, i);
                }

                //// All rules to fire until a fixpoint is reached.
                //// int printCount = 0;
                while (pendingAct.Count > 0)
                {
                    act = pendingAct.GetSomeElement();

                    /*
                    if (printCount == 2000)
                    {
                        printCount = 0;
                        Console.WriteLine("{0} {1}", pendingAct.Count, facts.Count);
                    }
                    else
                    {
                        printCount++;
                    }
                    */

                    pendingAct.Remove(act);
                    act.Rule.Execute(act.Binding, act.FindNumber, this, KeepDerivations, pendingFacts);
                    foreach (var kv in pendingFacts)
                    {
                        //// Console.WriteLine("Adding {0} by {1}", kv.Key.Debug_GetSmallTermString(), act.Rule.RuleId);
                        IndexFact(kv.Key, kv.Value, pendingAct, i);
                    }

                    pendingFacts.Clear();
                }
            }

            /*
            Console.WriteLine("--------------------- FIXPOINT ---------------------");
            UserSymbol us;
            foreach (var f in facts.Keys)
            {
                us = f.Symbol as UserSymbol;
                if (us != null && (us.Namespace.Parent != null || !us.IsAutoGen))
                {
                    Console.WriteLine(us.FullName + "(");
                    for (int i = 0; i < us.Arity; ++i)
                    {
                        Console.WriteLine("\t" + f.Args[i].Debug_GetSmallTermString() + ",");
                    }
                    Console.WriteLine();

                    //// Console.WriteLine(f.Debug_GetSmallTermString());
                    //foreach (var d in GetDerivations(f))
                    //{
                    //    Console.WriteLine("--- Derivation ---");
                    //    d.Debug_PrintTree();
                    //}
                }
                // else
                // {
                //    Console.WriteLine(f.Debug_GetSmallTermString());
                // }
            }
            */
        }

        /// <summary>
        /// Returns true if the fact t is in the index.
        /// </summary>
        public bool Exists(Term t)
        {
            Contract.Requires(t != null);
            return facts.ContainsKey(t);
        }

        /// <summary>
        /// Returns true if the fact t is in the index.
        /// If the fact is in the index and derivations are being tracked,
        /// then records this derivation of t.
        /// </summary>
        public bool IfExistsThenDerive(Term t, Derivation d)
        {
            Contract.Requires(t != null);
            if (!KeepDerivations)
            {
                return facts.ContainsKey(t);
            }

            Set<Derivation> dervs;
            if (!facts.TryFindValue(t, out dervs))
            {
                return false;
            }

            dervs.Add(d);
            return true;
        }

        /// <summary>
        /// Queries all the terms matching a find pattern.
        /// </summary>
        public IEnumerable<Term> Query(Term pattern, Term[] projection)
        {
            /*
            Console.Write("Query {0}: [", pattern.Debug_GetSmallTermString());
            foreach (var t in projection)
            {
                Console.Write(" " + t.Debug_GetSmallTermString());
            }

            Console.WriteLine(" ]");
            */

            return trigIndices[pattern].Query(projection);
        }

        /// <summary>
        /// Queries all the terms matching the type. Binding is an optional term that fixed 
        /// determines the queried fact.
        /// </summary>
        public IEnumerable<Term> Query(Term type, Term binding)
        {
            /*
            if (binding == null)
            {
                Console.WriteLine("Query type {0}", type.Debug_GetSmallTermString());
            }
            else
            {
                Console.WriteLine("Query type {0} = {1}", type.Debug_GetSmallTermString(), binding.Debug_GetSmallTermString());
            }
            */

            var patterns = typesToTriggersMap[type];
            if (binding != null)
            {
                foreach (var p in patterns)
                {
                    if (p.Symbol == binding.Symbol && Exists(binding))
                    {
                        yield return binding;
                        yield break;
                    }
                }

                yield break;
            }

            foreach (var p in patterns)
            {
                var results = trigIndices[p].Query(SubIndex.EmptyProjection);
                foreach (var t in results)
                {
                    yield return t;
                }
            }
        }

        /// <summary>
        /// Queries all the terms matching a comprehension.
        /// </summary>
        public IEnumerable<Term> Query(Term comprTerm, out int nResults)
        {
            Contract.Requires(comprTerm != null);
            var subIndex = comprIndices[comprTerm.Symbol];
            var projection = new Term[comprTerm.Symbol.Arity - 1];

            //// Console.Write("Query {0}: [", subIndex.Pattern.Debug_GetSmallTermString());
            for (int i = 0; i < comprTerm.Symbol.Arity - 1; ++i)
            {
                //// Console.Write(" " + comprTerm.Args[i].Debug_GetSmallTermString());
                projection[i] = comprTerm.Args[i];
            }

            //// Console.WriteLine(" ]");

            return subIndex.Query(projection, out nResults);
        }

        /// <summary>
        /// Returns a subindex pattern for looking up matching terms when 
        /// boundVars are already bound.
        /// </summary>
        public static Term MkPattern(Term t, Set<Term> boundVars)
        {
            Contract.Requires(t != null);

            int i;
            bool wasAdded;
            Term renaming;
            var index = t.Owner;
            int nextPVarId = 0, nextVarId = 0;
            var rnmMap = new Map<Term, Term>(Term.Compare);
            return t.Compute<Term>(
                (x, s) => x.Groundness == Groundness.Variable ? x.Args : null,
                (x, ch, s) =>
                {
                    if (x.Groundness != Groundness.Variable)
                    {
                        return x;
                    }
                    else if (x.Symbol.IsVariable)
                    {
                        if (!rnmMap.TryFindValue(x, out renaming))
                        {
                            if (boundVars != null && boundVars.Contains(x))
                            {
                                renaming = index.MkVar(PatternVarBoundPrefix + nextPVarId.ToString(), true, out wasAdded);
                                ++nextPVarId;
                            }
                            else
                            {
                                renaming = index.MkVar(PatternVarUnboundPrefix + nextVarId.ToString(), true, out wasAdded);
                                ++nextVarId;
                            }

                            rnmMap.Add(x, renaming);
                        }

                        return renaming;
                    }

                    i = 0;
                    var args = new Term[x.Symbol.Arity];
                    foreach (var tp in ch)
                    {
                        args[i++] = tp;
                    }

                    return index.MkApply(x.Symbol, args, out wasAdded);
                });
        }

        /// <summary>
        /// Enumerates proofs for the ground term t. Requires KeepDerivations.
        /// </summary>
        public IEnumerable<ProofTree> GetDerivations(Term goal)
        {
            Contract.Requires(KeepDerivations);
            Contract.Requires(goal != null && goal.Owner == TermIndex && goal.Groundness == Groundness.Ground);
            Set<Derivation> goalDervs;
            if (!facts.TryFindValue(goal, out goalDervs))
            {
                yield break;
            }

            ProofState p;
            LinkedListNode<ProofState> n, m, backtrack = null;
            bool isFullyExpanded;
            var root = new ProofState(goalDervs);
            var branch = new LinkedList<ProofState>();
            n = branch.AddLast(root);
            while (n != null)
            {
                m = n;
                //// This loop continues until all subgoals are expanded.
                //// Then n is pointing at the leaf of the rightmost fact in the proof.
                //// The proof state list contains the proof states reaching n.
                isFullyExpanded = true;
                while (m != null)
                {
                    if ((p = m.Value.ExpandSubgoal(branch, this, out isFullyExpanded)) != null)
                    {
                        while (branch.Last != m)
                        {
                            branch.RemoveLast();
                        }

                        m = n = branch.AddLast(p);
                    }
                    else if (isFullyExpanded)
                    {
                        m = m.Previous;
                    }
                    else
                    {
                        break;
                    }
                }

                if (isFullyExpanded)
                {
                    yield return MkProof(root, goal); //// Return proof
                }
                else
                {
                    Contract.Assert(backtrack != null);
                    while (branch.Last != backtrack)
                    {
                        branch.RemoveLast();
                        Contract.Assert(branch.Count > 0);
                    }
                }

                while (branch.Count > 0)
                {
                    m = branch.Last;
                    if ((p = m.Value.MoveChoice(branch, this)) != null)
                    {
                        backtrack = m;
                        n = branch.AddLast(p);
                        break;
                    }
                    else if ((p = m.Value.DropSubproof()) != null)
                    {
                        p.AppendRightmostBranch(branch);
                    }
                    else
                    {
                        n = null;
                        branch.RemoveLast();
                    }
                }            
            }            
        }

        public void Debug_PrintIndex()
        {
            Console.WriteLine("----------------- Facts -----------------");
            foreach (var t in facts.Keys)
            {
                Console.WriteLine(t.Debug_GetSmallTermString());
            }

            Console.WriteLine("----------------- Subindices -----------------");
            Console.WriteLine("Untriggered rules");
            foreach (var kv in untrigRules)
            {
                Console.Write("\tIn stratum {0}:", kv.Key);
                foreach (var r in kv.Value)
                {
                    Console.Write(" {0}", r.RuleId);
                }

                Console.WriteLine();                
            }

            foreach (var kv in comprIndices)
            {
                kv.Value.Debug_PrintSubindex();
            }

            foreach (var kv in trigIndices)
            {
                kv.Value.Debug_PrintSubindex();
            }
        }

        public static bool IsUnboundPatternVariable(Term t)
        {
            if (!t.Symbol.IsVariable)
            {
                return false;
            }
            else
            {
                var name = ((UserSymbol)t.Symbol).Name;
                return string.IsNullOrEmpty(name) ? false : name[0] == PatternVarUnboundPrefix;
            }
        }

        private void InitializeExecuter(Func<Term, Term> symbCnstGetter, IEnumerable<Term> otherSymbCnsts = null)
        {
            foreach (var r in Rules.Rules)
            {
                foreach (var s in r.ComprehensionSymbols)
                {
                    Register(s);
                }

                if (r.Trigger1 == null && r.Trigger2 == null)
                {
                    Register(r);
                    continue;
                }

                if (r.Trigger1 != null)
                {
                    Register(r, 0);
                }

                if (r.Trigger2 != null)
                {
                    Register(r, 1);
                }
            }

            var factDerv = KeepDerivations ? new Derivation[] { new Derivation(TermIndex) } : null;
            foreach (var kv in factSets)
            {
                if (string.IsNullOrEmpty(kv.Key))
                {
                    foreach (var t in kv.Value.Facts)
                    {
                        Contract.Assert(t.Owner == TermIndex);
                        IndexFact(t, factDerv, null, -1);
                    }
                }
                else
                {
                    foreach (var t in kv.Value.Facts)
                    {
                        IndexFact(TermIndex.MkClone(t, kv.Key), factDerv, null, -1);
                    }
                }
            }

            bool wasAdded;
            foreach (var t in Rules.SymbolicConstants)
            {
                IndexFact(
                    TermIndex.MkApply(TermIndex.SCValueSymbol, new Term[] { t, symbCnstGetter(t) }, out wasAdded),
                    factDerv,
                    null,
                    -1);
            }

            if (otherSymbCnsts != null)
            {
                foreach (var t in otherSymbCnsts)
                {
                    IndexFact(
                        TermIndex.MkApply(TermIndex.SCValueSymbol, new Term[] { t, symbCnstGetter(t) }, out wasAdded),
                        factDerv,
                        null,
                        -1);
                }
            }
        }

        private void IndexFact(Term t, IEnumerable<Derivation> drs, Set<PendingActivation> pending, int stratum)
        {
            Set<Derivation> dervs;
            if (KeepDerivations)
            {
                if (!facts.TryFindValue(t, out dervs))
                {
                    dervs = new Set<Derivation>(Derivation.Compare);
                    facts.Add(t, dervs);
                }

                foreach (var d in drs)
                {
                    dervs.Add(d);
                }
            }
            else if (!facts.TryFindValue(t, out dervs))
            {
                facts.Add(t, null);
            }

            LinkedList<SubIndex> subindices;
            if (!symbToIndexMap.TryFindValue(t.Symbol, out subindices))
            {
                return;
            }

            foreach (var index in subindices)
            {
                index.TryAdd(t, pending, stratum);
            }
        }

        /*
        private void CheckRelConstraints()
        {
            ConSymb con;
            MapSymb map;
            bool isNotRel = false;
            Set<Derivation> dervs = KeepDerivations ? new Set<Derivation>(Derivation.Compare) : null;

            Term f;
            foreach (var kv in facts)
            {
                f = kv.Key;
                foreach (var t in f.Enumerate(x => x.Args))
                {
                    if (t.Symbol.Kind == SymbolKind.ConSymb)
                    {
                        con = (ConSymb)t.Symbol;
                        if (con.IsAutoGen || !con.IsNew)
                        {
                            continue;
                        }

                        for (int i = 0; i < con.Arity; ++i)
                        {
                            if (!con.IsAnyArg(i) && t.Args[i].Symbol.IsDataConstructor && !facts.ContainsKey(t.Args[i]))
                            {
                                isNotRel = true;
                                if (KeepDerivations)
                                {
                                    dervs.UnionWith(kv.Value);
                                }

                                break;
                            }
                        }                        
                    }
                    else if (t.Symbol.Kind == SymbolKind.MapSymb)
                    {
                        map = (MapSymb)t.Symbol;
                        if (map.IsAutoGen)
                        {
                            continue;
                        }

                        for (int i = 0; i < map.Arity; ++i)
                        {
                            if (!map.IsAnyArg(i) && t.Args[i].Symbol.IsDataConstructor && !facts.ContainsKey(t.Args[i]))
                            {
                                isNotRel = true;
                                if (KeepDerivations)
                                {
                                    dervs.UnionWith(kv.Value);
                                }

                                break;
                            }
                        }                        
                    }
                }

                if (!KeepDerivations && isNotRel)
                {
                    break;
                }
            }

            if (!isNotRel)
            {
                return;
            }

            Namespace modSpace, otherSpace;
            modSpace = TermIndex.SymbolTable.Resolve(
                factSets.Model.Node.Domain.Name,
                out otherSpace);
            Contract.Assert(modSpace != null && otherSpace == null);
             
            UserSymbol notRelSymb, otherSymb;
            notRelSymb = TermIndex.SymbolTable.Resolve(SymbolTable.NotRelCnstrName, out otherSymb, modSpace);
            Contract.Assert(notRelSymb != null && otherSymb == null);

            bool wasAdded;
            facts.Add(TermIndex.MkApply(notRelSymb, TermIndex.EmptyArgs, out wasAdded), dervs);
        }
        */

        private ProofTree MkProof(ProofState root, Term goal)
        {
            Contract.Requires(root != null && root.Derivation == null);
            var proofTreeStack = new Stack<ProofTree>();
            var proofStateStack = new Stack<MutableTuple<ProofState, int>>();

            proofTreeStack.Push(new ProofTree(goal, root.CurrentSubProofs[0].Derivation.Rule.Node));
            proofStateStack.Push(new MutableTuple<ProofState, int>(root.CurrentSubProofs[0], -1));

            ProofTree treeTop;
            Term binding, bindingVar;
            MutableTuple<ProofState, int> stateTop;
            while (proofStateStack.Count > 0)
            {
                stateTop = proofStateStack.Peek();
                if (stateTop.Item2 >= 0)
                {
                    if (stateTop.Item1.GetBinding(stateTop.Item2, out bindingVar) != null &&
                        !bindingVar.Symbol.IsReservedOperation)
                    {
                        Contract.Assert(bindingVar.Symbol.IsVariable);
                        treeTop = proofTreeStack.Pop();
                        proofTreeStack.Peek().AddSubproof(
                            ((UserSymbol)bindingVar.Symbol).Name,
                            treeTop);
                    }
                }

                stateTop.Item2++;
                if (stateTop.Item2 < stateTop.Item1.NSubgoals)
                {
                    binding = stateTop.Item1.GetBinding(stateTop.Item2, out bindingVar);
                    if (!bindingVar.Symbol.IsReservedOperation)
                    {
                        if (stateTop.Item1.CurrentSubProofs[stateTop.Item2].Derivation.Rule == null)
                        {
                            proofTreeStack.Push(
                                new ProofTree(binding, Factory.Instance.MkId("?", new Span(0, 0, 0, 0)).Node));
                        }
                        else
                        {
                            proofTreeStack.Push(
                                new ProofTree(binding, stateTop.Item1.CurrentSubProofs[stateTop.Item2].Derivation.Rule.Node));
                        }
                    }

                    proofStateStack.Push(new MutableTuple<ProofState, int>(stateTop.Item1.CurrentSubProofs[stateTop.Item2], -1));
                }
                else
                {
                    proofStateStack.Pop();
                }
            }

            Contract.Assert(proofTreeStack.Count == 1);
            return proofTreeStack.Pop();
        }

        /// <summary>
        /// Makes a subindex pattern for a symbol s. 
        /// If the symb is a comprehension symbol then the pattern is s(^0,...,^n-1, *0).
        /// Otherwise it is s(*0,...,*n).
        /// </summary>
        private Term MkPattern(Symbol s, bool isCompr)
        {
            Contract.Requires(s != null && (!isCompr || s.Arity > 0));
            bool wasAdded;
            var args = new Term[s.Arity];

            if (isCompr)
            {
                for (int i = 0; i < s.Arity - 1; ++i)
                {
                    args[i] = TermIndex.MkVar(PatternVarBoundPrefix + i.ToString(), true, out wasAdded);
                }

                args[s.Arity - 1] = TermIndex.MkVar(PatternVarUnboundPrefix + "0", true, out wasAdded);
            }
            else
            {
                for (int i = 0; i < s.Arity; ++i)
                {
                    args[i] = TermIndex.MkVar(PatternVarUnboundPrefix + i.ToString(), true, out wasAdded);
                }
            }

            return TermIndex.MkApply(s, args, out wasAdded);
        }

        /// <summary>
        /// Register a rule without any finds.
        /// </summary>
        private void Register(CoreRule rule)
        {
            Contract.Requires(rule != null && rule.Trigger1 == null && rule.Trigger2 == null);
            LinkedList<CoreRule> untriggered;
            if (!untrigRules.TryFindValue(rule.Stratum, out untriggered))
            {
                untriggered = new LinkedList<CoreRule>();
                untrigRules.Add(rule.Stratum, untriggered);
            }

            untriggered.AddLast(rule);
        }

        /// <summary>
        /// Register a rule with a findnumber. The trigger may be constrained only by type constraints.
        /// </summary>
        private void Register(CoreRule rule, int findNumber)
        {
            Term trigger;
            Term type;
            switch (findNumber)
            {
                case 0:
                    trigger = rule.Trigger1;
                    type = rule.Find1.Type;
                    break;
                case 1:
                    trigger = rule.Trigger2;
                    type = rule.Find2.Type;
                    break;
                default:
                    throw new Impossible();
            }

            if (!trigger.Symbol.IsVariable)
            {
                Register(rule, trigger, findNumber);
                return;
            }

            Set<Term> patternSet;
            if (typesToTriggersMap.TryFindValue(type, out patternSet))
            {
                foreach (var p in patternSet)
                {
                    trigIndices[p].AddTrigger(rule, findNumber);
                }

                return;
            }

            Set<Symbol> triggerSymbols = new Set<Symbol>(Symbol.Compare);
            type.Visit(
                x => x.Symbol == TermIndex.TypeUnionSymbol ? x.Args : null,
                x =>
                {
                    if (x.Symbol != TermIndex.TypeUnionSymbol)
                    {
                        triggerSymbols.Add(x.Symbol);
                    }
                });

            Term pattern;
            patternSet = new Set<Term>(Term.Compare);
            foreach (var s in triggerSymbols)
            {
                if (s.Kind == SymbolKind.UserSortSymb)
                {
                    pattern = MkPattern(((UserSortSymb)s).DataSymbol, false);
                    patternSet.Add(pattern);
                    Register(rule, pattern, findNumber);
                }
                else
                {
                    Contract.Assert(s.IsDataConstructor || s.IsNonVarConstant);
                    pattern = MkPattern(s, false);
                    patternSet.Add(pattern);
                    Register(rule, pattern, findNumber);
                }
            }

            typesToTriggersMap.Add(type, patternSet);
        }

        /// <summary>
        /// Register a rule triggered by a find in position findnumber
        /// </summary>
        private void Register(CoreRule rule, Term trigger, int findNumber)
        {
            Contract.Requires(rule != null && trigger != null);

            SubIndex index;
            if (!trigIndices.TryFindValue(trigger, out index))
            {
                index = new SubIndex(trigger);
                trigIndices.Add(trigger, index);

                LinkedList<SubIndex> subindices;
                if (!symbToIndexMap.TryFindValue(index.Pattern.Symbol, out subindices))
                {
                    subindices = new LinkedList<SubIndex>();
                    symbToIndexMap.Add(index.Pattern.Symbol, subindices);
                }

                subindices.AddLast(index);
            }

            index.AddTrigger(rule, findNumber);
        }

        /// <summary>
        /// Register a comprehension symbol
        /// </summary>
        private void Register(Symbol comprSymbol)
        {
            Contract.Requires(comprSymbol != null);
            SubIndex index;
            if (!comprIndices.TryFindValue(comprSymbol, out index))
            {
                index = new SubIndex(MkPattern(comprSymbol, true));
                comprIndices.Add(comprSymbol, index);

                LinkedList<SubIndex> subindices;
                if (!symbToIndexMap.TryFindValue(comprSymbol, out subindices))
                {
                    subindices = new LinkedList<SubIndex>();
                    symbToIndexMap.Add(comprSymbol, subindices);
                }

                subindices.AddLast(index);
            }
        }

        /*
        private void CheckRelConstrs()
        {
            bool isNotRelational
        }
        */

        private static int Compare(Tuple<CoreRule, int> p1, Tuple<CoreRule, int> p2)
        {
            var cmp = p1.Item1.RuleId - p2.Item1.RuleId;
            if (cmp != 0)
            {
                return cmp;
            }

            return p1.Item2 - p2.Item2;
        }

        private class ProofState
        {
            private int lastExpSubgoal = -1;
            private Set<Derivation>[] choices;
            private IEnumerator<Derivation>[] crntSubGoals;
            private ProofState[] crntSubProofs;

            public int NSubgoals
            {
                get { return choices.Length; }
            }

            public ProofState[] CurrentSubProofs
            {
                get { return crntSubProofs; }
            }

            public Derivation Derivation
            {
                get;
                private set;
            }

            public ProofState(Executer index, Derivation d)
            {
                Derivation = d;

                if (d.Binding1 != index.TermIndex.FalseValue &&
                    d.Binding2 != index.TermIndex.FalseValue)
                {
                    choices = new Set<Derivation>[2];
                    crntSubGoals = new IEnumerator<Derivation>[2];
                    crntSubProofs = new ProofState[2];
                    
                    crntSubGoals[0] = crntSubGoals[1] = null;
                    crntSubProofs[0] = crntSubProofs[1] = null;
                    choices[0] = index.facts[d.Binding1];
                    choices[1] = index.facts[d.Binding2];
                    Contract.Assert(choices[0].Count > 0 && choices[1].Count > 0);
                }
                else if (d.Binding1 != index.TermIndex.FalseValue)
                {
                    choices = new Set<Derivation>[1];
                    crntSubGoals = new IEnumerator<Derivation>[1];
                    crntSubProofs = new ProofState[1];

                    crntSubGoals[0] = null;
                    crntSubProofs[0] = null;
                    choices[0] = index.facts[d.Binding1];
                    Contract.Assert(choices[0].Count > 0);
                }
                else if (d.Binding2 != index.TermIndex.FalseValue)
                {
                    choices = new Set<Derivation>[1];
                    crntSubGoals = new IEnumerator<Derivation>[1];
                    crntSubProofs = new ProofState[1];

                    crntSubGoals[0] = null;
                    crntSubProofs[0] = null;
                    choices[0] = index.facts[d.Binding2];
                    Contract.Assert(choices[0].Count > 0);
                }
                else
                {
                    choices = new Set<Derivation>[0];
                    crntSubGoals = new IEnumerator<Derivation>[0];
                    crntSubProofs = new ProofState[0];
                }
            }

            public ProofState(Set<Derivation> goals)
            {
                Derivation = null;

                choices = new Set<Derivation>[] { goals };
                crntSubGoals = new IEnumerator<Derivation>[1];
                crntSubProofs = new ProofState[1];

                crntSubGoals[0] = null;
                crntSubProofs[0] = null;
            }

            public Term GetBinding(int index, out Term bindingVar)
            {
                Contract.Requires(Derivation != null);
                Contract.Requires(index >= 0 && index < CurrentSubProofs.Length);

                if (index == 0)
                {
                    if (Derivation.Binding1 != Derivation.Binding1.Owner.FalseValue)
                    {
                        bindingVar = Derivation.Rule.Find1.Binding;
                        return Derivation.Binding1;
                    }
                    else
                    {
                        bindingVar = Derivation.Rule.Find2.Binding;
                        return Derivation.Binding2;
                    }
                }
                else
                {
                    bindingVar = Derivation.Rule.Find2.Binding;
                    return Derivation.Binding2;
                }
            }

            public void AppendRightmostBranch(LinkedList<ProofState> branch)
            {
                var n = this;
                while (n != null)
                {
                    branch.AddLast(n);
                    n = n.lastExpSubgoal == -1 ? null : n.crntSubProofs[n.lastExpSubgoal];
                }
            }

            /// <summary>
            /// Drops the proof of the ith subgoal and returns the current proof at i - 1. Returns null if i - 1 is not defined. 
            /// </summary>
            public ProofState DropSubproof()
            {
                if (lastExpSubgoal == -1)
                {
                    return null;
                }

                crntSubProofs[lastExpSubgoal] = null;
                crntSubGoals[lastExpSubgoal] = null;

                --lastExpSubgoal;
                if (lastExpSubgoal == -1)
                {
                    return null;
                }
                else
                {
                    return crntSubProofs[lastExpSubgoal];
                }
            }

            public ProofState MoveChoice(LinkedList<ProofState> branch, Executer index)
            {
                if (lastExpSubgoal == -1)
                {
                    return null;
                }

                bool moveAgain = true;
                LinkedListNode<ProofState> n;
                while (moveAgain)
                {
                    if (!crntSubGoals[lastExpSubgoal].MoveNext())
                    {
                        return null;
                    }

                    moveAgain = false;

                    //// Backup to the place where this subgoal is being expanded.
                    n = branch.Last;
                    while (n != null)
                    {
                        if (n.Value == this)
                        {
                            break;
                        }
                        else
                        {
                            n = n.Previous;
                        }
                    }

                    while (n != null)
                    {
                        if (n.Value.Derivation == null)
                        {
                            n = n.Previous;
                            continue;
                        }

                        if (Derivation.Compare(n.Value.Derivation, crntSubGoals[lastExpSubgoal].Current) == 0)
                        {
                            moveAgain = true;
                            break;
                        }
                        else
                        {
                            n = n.Previous;
                        }
                    }
                }

                return crntSubProofs[lastExpSubgoal] = new ProofState(index, crntSubGoals[lastExpSubgoal].Current);

                /*
                if (crntSubGoals[lastExpSubgoal].MoveNext())
                {
                    return crntSubProofs[lastExpSubgoal] = new ProofState(index, crntSubGoals[lastExpSubgoal].Current);
                }
                 
                return null;
                */
            }
            
            public ProofState ExpandSubgoal(LinkedList<ProofState> branch, Executer index, out bool isFullyExpanded)
            {
                if (lastExpSubgoal == choices.Length - 1)
                {
                    isFullyExpanded = true;
                    return null;
                }

                ++lastExpSubgoal;
                isFullyExpanded = false;
                bool moveAgain = true;
                LinkedListNode<ProofState> n;
                crntSubGoals[lastExpSubgoal] = choices[lastExpSubgoal].GetEnumerator();

                while (moveAgain)
                {
                    if (!(moveAgain = crntSubGoals[lastExpSubgoal].MoveNext()))
                    {
                        --lastExpSubgoal;
                        return null;
                    }

                    moveAgain = false;

                    //// Backup to the place where this subgoal is being expanded.
                    n = branch.Last;
                    while (n != null)
                    {
                        if (n.Value == this)
                        {
                            break;
                        }
                        else
                        {
                            n = n.Previous;
                        }
                    }

                    while (n != null)
                    {
                        if (n.Value.Derivation == null)
                        {
                            n = n.Previous;
                            continue;
                        }

                        if (Derivation.Compare(n.Value.Derivation, crntSubGoals[lastExpSubgoal].Current) == 0)
                        {
                            moveAgain = true;
                            break;
                        }
                        else
                        {
                            n = n.Previous;
                        }
                    }
                }

                return crntSubProofs[lastExpSubgoal] = new ProofState(index, crntSubGoals[lastExpSubgoal].Current);
            }
        }

        private class PendingActivation
        {
            public CoreRule Rule
            {
                get;
                private set;
            }

            public int FindNumber
            {
                get;
                private set;
            }

            public Term Binding
            {
                get;
                private set;
            }

            public PendingActivation(CoreRule rule, int findNumber, Term binding)
            {
                Rule = rule;
                FindNumber = findNumber;
                Binding = binding;
            }

            /*
            public static int Compare(PendingActivation a1, PendingActivation a2)
            {
                var cmp = a1.Rule.RuleId - a2.Rule.RuleId;
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = a1.FindNumber - a2.FindNumber;
                if (cmp != 0)
                {
                    return cmp;
                }

                return Term.Compare(a1.Binding, a2.Binding);
            }
            */

            public static int Compare(PendingActivation a1, PendingActivation a2)
            {
                var cmp = Term.Compare(a1.Binding, a2.Binding);
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = a1.Rule.RuleId - a2.Rule.RuleId;
                if (cmp != 0)
                {
                    return cmp;
                }

                return a1.FindNumber - a2.FindNumber;
            }
        }

        private class SubIndex
        {
            public static readonly Term[] EmptyProjection = new Term[0];

            private int nBoundVars;
            private Matcher patternMatcher;

            /// <summary>
            /// Map from strata to rules triggered by this pattern.
            /// </summary>
            private Map<int, LinkedList<Tuple<CoreRule, int>>> triggers =
                new Map<int, LinkedList<Tuple<CoreRule, int>>>((x, y) => x - y);

            /// <summary>
            /// Groups terms by projections according to the pattern.
            /// </summary>
            private Map<Term[], Set<Term>> facts = new Map<Term[], Set<Term>>(Compare);

            /// <summary>
            /// The pattern of this subindex.
            /// </summary>
            public Term Pattern
            {
                get;
                private set;
            }

            public SubIndex(Term pattern)
            {
                Pattern = pattern;
                patternMatcher = new Matcher(pattern);
                nBoundVars = 0;
                foreach (var kv in patternMatcher.CurrentBindings)
                {
                    if (((UserSymbol)kv.Key.Symbol).Name[0] == Executer.PatternVarBoundPrefix)
                    {
                        ++nBoundVars;
                    }
                }
            }

            public IEnumerable<Term> Query(Term[] projection)
            {
                Set<Term> subindex;
                if (!facts.TryFindValue(projection, out subindex))
                {
                    yield break;
                }

                foreach (var t in subindex)
                {
                    yield return t;
                }
            }

            public IEnumerable<Term> Query(Term[] projection, out int nResults)
            {
                Set<Term> subindex;
                if (!facts.TryFindValue(projection, out subindex))
                {
                    nResults = 0;
                }
                else 
                {
                    nResults = subindex.Count;
                }

                return Query(projection);
            }

            public void AddTrigger(CoreRule rule, int findNumber)
            {
                LinkedList<Tuple<CoreRule, int>> rules;
                if (!triggers.TryFindValue(rule.Stratum, out rules))
                {
                    rules = new LinkedList<Tuple<CoreRule, int>>();
                    triggers.Add(rule.Stratum, rules);
                }

                rules.AddLast(new Tuple<CoreRule, int>(rule, findNumber));
            }

            /// <summary>
            /// Tries to add this term to the subindex. Returns true if t matches the pattern.
            /// If pending is non-null, then pends rules that are triggered by this term.
            /// </summary>
            public bool TryAdd(Term t, Set<PendingActivation> pending, int stratum)
            {
                if (!patternMatcher.TryMatch(t))
                {
                    return false;
                }

                Term[] projection;
                if (nBoundVars == 0)
                {
                    projection = EmptyProjection;
                }
                else
                {
                    int i = 0;
                    projection = new Term[nBoundVars];
                    foreach (var kv in patternMatcher.CurrentBindings)
                    {
                        if (((UserSymbol)kv.Key.Symbol).Name[0] == Executer.PatternVarBoundPrefix)
                        {
                            projection[i++] = kv.Value;
                        }
                    }
                }

                Set<Term> subset;
                if (!facts.TryFindValue(projection, out subset))
                {
                    subset = new Set<Term>(Term.Compare);
                    facts.Add(projection, subset);
                }

                subset.Add(t);

                if (pending != null)
                {
                    LinkedList<Tuple<CoreRule, int>> triggered;
                    if (triggers.TryFindValue(stratum, out triggered))
                    {
                        foreach (var trig in triggered)
                        {
                            pending.Add(new PendingActivation(trig.Item1, trig.Item2, t));
                        }
                    }
                }

                return true;
            }

            /// <summary>
            /// Generate pending activations for all rules triggered in this stratum. 
            /// </summary>
            public void PendAll(Set<PendingActivation> pending, int stratum)
            {
                LinkedList<Tuple<CoreRule, int>> triggered;
                if (!triggers.TryFindValue(stratum, out triggered))
                {
                    return;
                }

                foreach (var kv in facts)
                {
                    foreach (var t in kv.Value)
                    {
                        foreach (var trig in triggered)
                        {
                            pending.Add(new PendingActivation(trig.Item1, trig.Item2, t));
                        }
                    }
                }
            }

            public static int Compare(Term[] v1, Term[] v2)
            {
                return EnumerableMethods.LexCompare<Term>(v1, v2, Term.Compare);
            }

            public void Debug_PrintSubindex()
            {
                Console.WriteLine(
                    "----------------- Subindex {0} ----------------- ", 
                    Pattern.Debug_GetSmallTermString());

                foreach (var kv in triggers)
                {
                    Console.Write("\tTriggered in stratum {0}:", kv.Key);
                    foreach (var kvp in kv.Value)
                    {
                        Console.Write(" ({0}, {1})", kvp.Item1.RuleId, kvp.Item2);
                    }

                    Console.WriteLine();
                }

                Console.WriteLine("Facts");
                foreach (var kv in facts)
                {
                    Console.Write("[");
                    foreach (var t in kv.Key)
                    {
                        Console.Write(" " + t.Debug_GetSmallTermString());
                    }

                    Console.WriteLine(" ] -->");
                    foreach (var t in kv.Value)
                    {
                        Console.WriteLine("\t" + t.Debug_GetSmallTermString());
                    }
                }
            }
        }
    }
}
