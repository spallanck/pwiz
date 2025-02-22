﻿/*
 * Original author: Tobias Rohde <tobiasr .at. uw.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2017 University of Washington - Seattle, WA
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using pwiz.Common.SystemUtil;
using pwiz.Skyline.Model;
using pwiz.Skyline.Model.Results;
using pwiz.Skyline.Util;

namespace pwiz.Skyline.Controls.Graphs
{
    public partial class AreaCVGraphData
    {
        private class CacheInfo
        {
            public CacheInfo()
            {
                Data = new List<AreaCVGraphData>();
                Document = null;
                Settings = null;
            }

            public void Clear()
            {
                Data.Clear();
                Document = null;
                Settings = null;
            }

            public readonly List<AreaCVGraphData> Data;
            public SrmDocument Document { get; set; }
            public AreaCVGraphSettings Settings { get; set; }
        }

        public class AreaCVGraphDataCache : IDisposable
        {
            private readonly StackWorker<GraphDataProperties> _producerConsumer;
            private readonly CacheInfo _cacheInfo;
            private CancellationTokenSource _tokenSource;

            private readonly object _requestLock = new object();
            private GraphDataProperties _requested;
            private Action<AreaCVGraphData> _callback;

            private static readonly int MAX_THREADS =
                ParallelEx.SINGLE_THREADED ? 1 : Math.Max(1, Environment.ProcessorCount / 2);

            public AreaCVGraphDataCache()
            {
                _producerConsumer = new StackWorker<GraphDataProperties>(null, CacheData);
                _producerConsumer.RunAsync(MAX_THREADS, @"AreaCVGraphDataCache");
                _cacheInfo = new CacheInfo();
                _tokenSource = new CancellationTokenSource();
            }

            public bool TryGet(SrmDocument document, AreaCVGraphSettings settings, Action<AreaCVGraphData> callback, out AreaCVGraphData result)
            {
                var properties = new GraphDataProperties(settings);
                if (!IsValidFor(document, settings))
                {
                    Cancel();

                    lock (_cacheInfo)
                    {
                        _cacheInfo.Clear();
                        _cacheInfo.Document = document;
                        _cacheInfo.Settings = settings;
                    }

                    // Get a list of all properties that we want to cache data for, except for the data that just got requested
                    var propertyList = new List<GraphDataProperties>(GetPropertyVariants(settings).Except(new[] { properties }));
                    _producerConsumer.Add(propertyList, false, false);
                }


                result = Get(properties);
                if (result != null)
                    return true;
                
                lock (_requestLock)
                {
                    if (!properties.Equals((object) _requested))
                    {
                        _producerConsumer.Add(properties);

                        _requested = properties;
                        _callback = callback;
                    }

                    return false;
                }
            }

            private AreaCVGraphData CreateOrGet(GraphDataProperties properties)
            {
                var result = Get(properties);
                if (result == null)
                {
                    SrmDocument document;
                    AreaCVGraphSettings settings;
                    lock (_cacheInfo)
                    {
                        document = _cacheInfo.Document;
                        settings = _cacheInfo.Settings;
                    }

                    result = new AreaCVGraphData(document,
                        new AreaCVGraphSettings(settings.GraphType,
                            properties.NormalizeOption,
                            settings.Group,
                            properties.Annotation,
                            settings.PointsType,
                            settings.QValueCutoff,
                            settings.CVCutoff,
                            properties.MinimumDetections,
                            settings.BinWidth,
                            settings.MsLevel,
                            settings.Transitions,
                            settings.CountTransitions), _tokenSource.Token);

                    lock (_cacheInfo)
                    {
                        if(IsValidFor(document, settings))
                            _cacheInfo.Data.Add(result);
                    }
                }

                return result;
            }

            public AreaCVGraphData Get(GraphDataProperties properties)
            {
                return Get(properties.Group, properties.Annotation, properties.MinimumDetections, properties.NormalizeOption);
            }

            public AreaCVGraphData Get(ReplicateValue group, object annotation, int minimumDetections, NormalizeOption normalizeOption)
            {
                lock (_cacheInfo)
                {
                    // Linear search, but very short list
                    return _cacheInfo.Data.FirstOrDefault(d => Equals(d._graphSettings.Group, group) &&
                                                               Equals(d._graphSettings.Annotation, annotation) &&
                                                               d._graphSettings.MinimumDetections ==
                                                               minimumDetections &&
                                                               d._graphSettings.NormalizeOption == normalizeOption);
                }
            }

            public bool IsValidFor(SrmDocument document, AreaCVGraphSettings settings)
            {
                lock (_cacheInfo)
                {
                    return _cacheInfo.Document != null && _cacheInfo.Settings != null &&
                           ReferenceEquals(_cacheInfo.Document.Children, document.Children) &&
                           AreaCVGraphSettings.CacheEqual(_cacheInfo.Settings, settings);
                }
            }

            private static int GetMinDetectionsForAnnotation(SrmDocument document, AreaCVGraphSettings graphSettings, object annotationValue)
            {
                return document.Settings.PeptideSettings.Integration.PeakScoringModel.IsTrained && !double.IsNaN(graphSettings.QValueCutoff)
                    ? AnnotationHelper.GetReplicateIndices(document, graphSettings.Group, annotationValue).Length
                    : 2;
            }

            private IEnumerable<GraphDataProperties> GetPropertyVariants(AreaCVGraphSettings graphSettings)
            {
                SrmDocument document;
                lock (_cacheInfo)
                {
                    document = _cacheInfo.Document;
                }

                var annotationsArray = AnnotationHelper.GetPossibleAnnotations(document, graphSettings.Group);

                // Add an entry for All
                var annotations = annotationsArray.Concat(new string[] { null }).ToList();

                var normalizationMethods = new List<NormalizeOption>(NormalizeOption.AvailableNormalizeOptions(document).Prepend(NormalizeOption.NONE));

                // First cache the histograms for the current annotation
                if (annotations.Remove(graphSettings.Annotation))
                    annotations.Insert(0, graphSettings.Annotation);

                foreach (var n in normalizationMethods)
                {
                    if (n.IsRatioToLabel && !document.Settings.PeptideSettings.Modifications.HasHeavyModifications)
                        continue;

                    foreach (var a in annotations)
                    {
                        var minDetections = GetMinDetectionsForAnnotation(document,graphSettings, a);

                        for (var i = 2; i <= minDetections; ++i)
                        {
                            yield return new GraphDataProperties(graphSettings.Group, n, a, i);
                        }
                    }
                }
            }

            private void CacheData(GraphDataProperties properties, int index)
            {
                var data = CreateOrGet(properties);

                lock (_requestLock)
                {
                    if (properties.Equals((object) _requested))
                    {
                        _callback(data);
                        _requested = null;
                        _callback = null;
                    }
                }
            }

            public void Cancel()
            {
                _tokenSource.Cancel();
                _producerConsumer.Wait();
                _tokenSource.Dispose();
                _tokenSource = new CancellationTokenSource();
            }

            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                // Will only be called from UI thread, so it's safe to not have a lock
                if (!IsDisposed)
                {
                    _tokenSource.Cancel();
                    _producerConsumer.Dispose();
                    _tokenSource.Dispose();
                    lock (_cacheInfo)
                    {
                        _cacheInfo.Clear();
                    }
                    IsDisposed = true;
                }
            }

            public class GraphDataProperties
            {
                public bool Equals(GraphDataProperties other)
                {
                    return Equals(Group, other.Group) && NormalizeOption == other.NormalizeOption && Equals(Annotation, other.Annotation) && MinimumDetections == other.MinimumDetections;
                }

                public override bool Equals(object obj)
                {
                    if (ReferenceEquals(null, obj)) return false;
                    return obj is GraphDataProperties && Equals((GraphDataProperties)obj);
                }

                public override int GetHashCode()
                {
                    unchecked
                    {
                        var hashCode = (Group != null ? Group.GetHashCode() : 0);
                        hashCode = (hashCode * 397) ^ NormalizeOption.GetHashCode();
                        hashCode = (hashCode * 397) ^ (Annotation != null ? Annotation.GetHashCode() : 0);
                        hashCode = (hashCode * 397) ^ MinimumDetections;
                        return hashCode;
                    }
                }

                public GraphDataProperties(AreaCVGraphSettings settings)
                {
                    Group = settings.Group;
                    NormalizeOption = settings.NormalizeOption;
                    Annotation = settings.Annotation;
                    MinimumDetections = settings.MinimumDetections;
                }

                public GraphDataProperties(ReplicateValue group, NormalizeOption normalizeOption, object annotation, int minimumDetections)
                {
                    Group = group;
                    NormalizeOption = normalizeOption;
                    Annotation = annotation;
                    MinimumDetections = minimumDetections;
                }

                public ReplicateValue Group { get; private set; }
                public NormalizeOption NormalizeOption { get; private set; }
                public object Annotation { get; private set; }
                public int MinimumDetections { get; private set; }
            }

            #region Functional test support

            public int DataCount
            {
                get
                {
                    lock (_cacheInfo)
                    {
                        return _cacheInfo.Data.Count;
                    }
                }
            }

            #endregion
        }
    }
}
