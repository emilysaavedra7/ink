﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace Inklewriter.Parsed
{
	// Base class for Knots and Stitches
	public abstract class FlowBase : Parsed.Object
	{
		public string name { get; protected set; }
		public List<Parsed.Object> content { get; protected set; }
        public Dictionary<string, VariableAssignment> variableDeclarations;

		public FlowBase (string name = null, List<Parsed.Object> topLevelObjects = null)
		{
			this.name = name;

			if (topLevelObjects == null) {
				topLevelObjects = new List<Parsed.Object> ();
			}
			this.content = topLevelObjects;

            variableDeclarations = new Dictionary<string, VariableAssignment> ();

			foreach (var child in this.content) {
				child.parent = this;

                var varDecl = child as VariableAssignment;
                if (varDecl != null && varDecl.isNewDeclaration) {
                    variableDeclarations [varDecl.variableName] = varDecl;
                }
			}
		}


        class GatheredLooseEnd
        {
            public Runtime.Divert divert;
            public Gather targetGather;
        }

        List<GatheredLooseEnd> _gatheredLooseEnds;

        class WeaveBlockRuntimeResult
        {
            public Runtime.Container rootContainer;
            public Runtime.Container currentContainer;
            public List<IWeavePoint> looseEnds;

            // Reference to FlowBase's instance
            List<GatheredLooseEnd> _gatheredLooseEnds;

            public WeaveBlockRuntimeResult(List<GatheredLooseEnd> gatheredLooseEnds) {
                rootContainer = currentContainer = new Runtime.Container();
                looseEnds = new List<IWeavePoint> ();
                _gatheredLooseEnds = gatheredLooseEnds;
            }

            public void StartGather(Gather gather)
            {
                var gatherContainer = gather.runtimeContainer;
                gatherContainer.name = "gather" + _gatherCount;
                _gatherCount++;

                // Consume loose ends: divert them to this gather
                foreach (IWeavePoint looseEnd in looseEnds) {

                    if (looseEnd.hasLooseEnd) {
                        var divert = new Runtime.Divert ();
                        looseEnd.runtimeContainer.AddContent (divert);

                        // Maintain a list of them so that we can resolve their paths later
                        _gatheredLooseEnds.Add (new GatheredLooseEnd{ divert = divert, targetGather = gather });

                    }
               }
                looseEnds.RemoveRange (0, looseEnds.Count);

                // Finally, add this gather to the main content, but only accessible
                // by name so that it isn't stepped into automatically, but only via
                // a divert from a loose end
                if (currentContainer.content.Count == 0) {
                    currentContainer.AddContent (gatherContainer);
                } else {
                    currentContainer.AddToNamedContentOnly (gatherContainer);
                }


                // Replace the current container itself
                currentContainer = gatherContainer;

                _latestLooseGather = gather;
            }

            public void AddWeavePoint(IWeavePoint weavePoint)
            {
                // Current level Gather
                if (weavePoint is Gather) {
                    StartGather ((Gather)weavePoint);
                } 

                // Current level choice
                else if (weavePoint is Choice) {
                    currentContainer.AddContent (((Choice)weavePoint).runtimeObject);
                }

                // TODO: Do further analysis on this weavePoint to determine whether
                // it really is a loose end (e.g. does it end in a divert)
                if (weavePoint.hasLooseEnd) {

                    looseEnds.Add (weavePoint);

                    // A gather stops becoming a loose end itself 
                    // once it gets a choice
                    if (_latestLooseGather != null && weavePoint is Choice) {
                        looseEnds.Remove (_latestLooseGather);
                    }
                }
            }

            Gather _latestLooseGather;
            int _gatherCount;
        }


        public override Runtime.Object GenerateRuntimeObject ()
        {
            var container = new Runtime.Container ();
            container.name = name;

            // Maintain a list of gathered loose ends so that we can resolve
            // their divert paths in ResolveReferences
            this._gatheredLooseEnds = new List<GatheredLooseEnd> ();

            bool initialKnotOrStitchEntered = false;

            // Run through content defined for this knot/stitch:
            //  - First of all, any initial content before a sub-stitch
            //    or any weave content is added to the main content container
            //  - The first inner knot/stitch is automatically entered, while
            //    the others are only accessible by an explicit divert
            //  - Any Choices and Gathers (i.e. IWeavePoint) found are 
            //    processsed by GenerateFlowContent.
            int contentIdx = 0;
            while (contentIdx < content.Count) {

                Parsed.Object obj = content [contentIdx];

                // Inner knots and stitches
                if (obj is FlowBase) {

                    var childFlow = (FlowBase)obj;

                    // First inner knot/stitch - automatically step into it
                    if (!initialKnotOrStitchEntered) {
                        container.AddContent (childFlow.runtimeObject);
                        initialKnotOrStitchEntered = true;
                    } 

                    // All other knots/stitches are only accessible by name:
                    // i.e. by explicit divert
                    else {
                        container.AddToNamedContentOnly ((Runtime.INamedContent) childFlow.runtimeObject);
                    }
                }

                // Choices and Gathers: Process as blocks of weave-like content
                else if (obj is IWeavePoint) {
                    var result = GenerateWeaveBlockRuntime (ref contentIdx, indentIndex: 0);
                    container.AddContent (result.rootContainer);
                } 

                // Normal content (defined at start)
                else {
                    container.AddContent (obj.runtimeObject);
                }

                contentIdx++;
            }
                
            return container;
        }

        // Initially called from main GenerateRuntimeObject
        // Generate a container of content for a particular indent level.
        // Recursive for further indentation levels.
        WeaveBlockRuntimeResult GenerateWeaveBlockRuntime(ref int contentIdx, int indentIndex)
        {
            var result = new WeaveBlockRuntimeResult (_gatheredLooseEnds);

            // Keep track of previous weave point (Choice or Gather)
            // at the current indentation level:
            //  - to add ordinary content to be nested under it
            //  - to add nested content under it when it's indented
            //  - to remove it from the list of loose ends when it has
            //    indented content since it's no longer a loose end
            IWeavePoint previousWeavePoint = null;

            // Iterate through content for the block at this level of indentation
            //  - Normal content is nested under Choices and Gathers
            //  - Blocks that are further indented cause recursion
            //  - Keep track of loose ends so that they can be diverted to Gathers
            while (contentIdx < content.Count) {
                
                Parsed.Object obj = content [contentIdx];

                // Choice or Gather
                if (obj is IWeavePoint) {
                    var weavePoint = (IWeavePoint)obj;
                    var weaveIndentIdx = weavePoint.indentationDepth - 1;

                    // Moving to outer level indent
                    if (weaveIndentIdx < indentIndex) {
                        return result;
                    }

                    // Inner level indentation
                    else if (weaveIndentIdx > indentIndex) {
                        var nestedResult = GenerateWeaveBlockRuntime (ref contentIdx, weaveIndentIdx);

                        // Add this inner block to current container
                        // (i.e. within the main container, or within the last defined Choice/Gather)
                        if (previousWeavePoint == null) {
                            result.currentContainer.AddContent (nestedResult.rootContainer);
                        } else {
                            previousWeavePoint.runtimeContainer.AddContent (nestedResult.rootContainer);
                        }

                        // Append the indented block's loose ends to our own
                        result.looseEnds.AddRange (nestedResult.looseEnds);

                        // Now there's a deeper indentation level, the previous weave point doesn't
                        // count as a loose end
                        if (previousWeavePoint != null) {
                            result.looseEnds.Remove (previousWeavePoint);
                        }
                        continue;
                    } 
                        
                    result.AddWeavePoint (weavePoint);
                   
                    previousWeavePoint = weavePoint;
                } 

                // Normal content
                else {
                    if (previousWeavePoint != null) {
                        previousWeavePoint.runtimeContainer.AddContent (obj.runtimeObject);
                    } else {
                        result.currentContainer.AddContent (obj.runtimeObject);
                    }

                }

                contentIdx++;
            }

            return result;
        }

        public override void ResolveReferences (Story context)
		{
			foreach (Parsed.Object obj in content) {
				obj.ResolveReferences (context); 
			}

            if (_gatheredLooseEnds != null) {
                foreach(GatheredLooseEnd looseEnd in _gatheredLooseEnds) {
                    looseEnd.divert.targetPath = looseEnd.targetGather.runtimeObject.path;
                }
            }

        }
	}
}

