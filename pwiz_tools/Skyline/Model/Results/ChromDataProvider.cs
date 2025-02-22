﻿/*
 * Original author: Brendan MacLean <brendanx .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2010 University of Washington - Seattle, WA
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
using System.Drawing;
using pwiz.Common.Chemistry;
using pwiz.Common.SystemUtil;
using pwiz.ProteowizardWrapper;

namespace pwiz.Skyline.Model.Results
{
    internal abstract class ChromDataProvider : IDisposable
    {
        private readonly int _startPercent;
        private readonly int _endPercent;
        protected readonly IProgressMonitor _loader;

        protected ChromDataProvider(ChromFileInfo fileInfo,
                                    IProgressStatus status,
                                    int startPercent,
                                    int endPercent,
                                    IProgressMonitor loader)
        {
            FileInfo = fileInfo;
            Status = status;

            _startPercent = startPercent;
            _endPercent = endPercent;
            _loader = loader;
        }

        protected void SetPercentComplete(int percent)
        {
            if (_loader.IsCanceled)
            {
                _loader.UpdateProgress(Status = Status.Cancel());
                throw new LoadCanceledException(Status);
            }

            percent = Math.Min(_endPercent, (_endPercent - _startPercent) * percent / 100 + _startPercent);
            if (Status.IsPercentComplete(percent))
                return;

            _loader.UpdateProgress(Status = Status.ChangePercentComplete(percent));
        }

        /// <summary>
        /// Notify the provider that the first pass is complete and determine whether the chromatogram
        /// list needs to be reloaded.
        /// </summary>
        /// <returns>True if the chromatogram list needs to be reloaded</returns>
        public virtual bool CompleteFirstPass()
        {
            return false;  // Do nothing by default.
        }

        public ChromFileInfo FileInfo { get; private set; }

        public IProgressStatus Status { get; protected set; }

        public abstract IEnumerable<ChromKeyProviderIdPair> ChromIds { get; }

        public virtual byte[] MSDataFileScanIdBytes { get { return new byte[0]; } }

        public virtual void SetRequestOrder(IList<IList<int>> orderedSets) { }

        public abstract bool GetChromatogram(int id, Target modifiedSequence, Color color, out ChromExtra extra, out TimeIntensities timeIntensities);

        public abstract double? MaxRetentionTime { get; }

        public abstract double? MaxIntensity { get; }

        public virtual double? TicArea { get { return FileInfo.TicArea; } }

        public abstract eIonMobilityUnits IonMobilityUnits { get; }

        public abstract bool IsProcessedScans { get; }

        public abstract bool IsSingleMzMatch { get; }

        public virtual bool HasMidasSpectra { get { return false; } }

        public virtual bool HasSonarSpectra { get { return false; } }

        public virtual bool IsSrm { get { return FileInfo.IsSrm; } }

        // Used for offering hints to user when document transition polarities don't agree with the raw data
        public abstract bool SourceHasPositivePolarityData { get; }
        public abstract bool SourceHasNegativePolarityData { get; }

        public abstract void ReleaseMemory();

        public abstract void Dispose();
    }

    public sealed class GlobalChromatogramExtractor
    {
        private MsDataFileImpl _dataFile;
        private const string TIC_CHROMATOGRAM_ID = @"TIC";
        private const string BPC_CHROMATOGRAM_ID = @"BPC";

        public GlobalChromatogramExtractor(MsDataFileImpl dataFile)
        {
            _dataFile = dataFile;
            IndexOffset = 0;

            if (dataFile.ChromatogramCount > 0 && dataFile.GetChromatogramId(0, out _) == TIC_CHROMATOGRAM_ID)
                TicChromatogramIndex = 0;
            if (dataFile.ChromatogramCount > 1 && dataFile.GetChromatogramId(1, out _) == BPC_CHROMATOGRAM_ID)
                BpcChromatogramIndex = 1;

            QcTraceByIndex = new SortedDictionary<int, MsDataFileImpl.QcTrace>();
            foreach (var qcTrace in dataFile.GetQcTraces() ?? new List<MsDataFileImpl.QcTrace>())
            {
                QcTraceByIndex[qcTrace.Index] = qcTrace;
            }
        }

        /// <summary>
        /// Since global chromatograms can share the same index as Skyline's target chromatograms (they both start at 0),
        /// the global chromatograms are given an index offset starting after the last target chromatogram
        /// </summary>
        public int IndexOffset { get; set; }

        public int? TicChromatogramIndex { get; set; }
        public int? BpcChromatogramIndex { get; }

        public IList<int> GlobalChromatogramIndexes
        {
            get
            {
                var result = new List<int>();
                if (TicChromatogramIndex.HasValue)
                    result.Add(TicChromatogramIndex.Value);
                if (BpcChromatogramIndex.HasValue)
                    result.Add(BpcChromatogramIndex.Value);
                return result;
            }
        }

        public IDictionary<int, MsDataFileImpl.QcTrace> QcTraceByIndex { get; }

        public string GetChromatogramId(int index, out int indexId)
        {
            return _dataFile.GetChromatogramId(index, out indexId);
        }

        public bool GetChromatogram(int index, out float[] times, out float[] intensities)
        {
            index -= IndexOffset;

            if (QcTraceByIndex.TryGetValue(index, out MsDataFileImpl.QcTrace qcTrace))
            {
                times = MsDataFileImpl.ToFloatArray(qcTrace.Times);
                intensities = MsDataFileImpl.ToFloatArray(qcTrace.Intensities);
            }
            else if (index == TicChromatogramIndex || index == BpcChromatogramIndex)
            {
                _dataFile.GetChromatogram(index, out _, out times, out intensities, true);
            }
            else
            {
                times = intensities = null;
            }

            return times != null;
        }

        /// <summary>
        /// Returns true if the TIC chromatogram present in the .raw file can be relied on
        /// for the calculation of total MS1 ion current.
        /// </summary>
        public bool IsTicChromatogramUsable()
        {
            if (!TicChromatogramIndex.HasValue)
            {
                return false;
            }

            float[] times;
            if (!GetChromatogram(TicChromatogramIndex.Value, out times, out _))
            {
                return false;
            }

            if (times.Length <= 1)
            {
                return false;
            }

            return true;
        }
    }

    internal sealed class ChromatogramDataProvider : ChromDataProvider
    {
        private readonly List<ChromKeyProviderIdPair> _chromIds = new List<ChromKeyProviderIdPair>();
        private readonly int[] _chromIndices;

        private MsDataFileImpl _dataFile;
        private GlobalChromatogramExtractor _globalChromatogramExtractor;

        private readonly bool _hasMidasSpectra;
        private readonly bool _hasSonarSpectra;
        private readonly bool _sourceHasNegativePolarityData;
        private readonly bool _sourceHasPositivePolarityData;
        private readonly eIonMobilityUnits _ionMobilityUnits;

        /// <summary>
        /// The number of chromatograms read so far.
        /// </summary>
        private int _readChromatograms;

        public ChromatogramDataProvider(MsDataFileImpl dataFile,
                                        ChromFileInfo fileInfo,
                                        IProgressStatus status,
                                        int startPercent,
                                        int endPercent,
                                        IProgressMonitor loader)
            : base(fileInfo, status, startPercent, endPercent, loader)
        {
            _dataFile = dataFile;
            _globalChromatogramExtractor = new GlobalChromatogramExtractor(dataFile);

            int len = dataFile.ChromatogramCount;
            _chromIndices = new int[len];

            bool fixCEOptForShimadzu = dataFile.IsShimadzuFile;
            int indexPrecursor = -1;
            var lastPrecursor = SignedMz.ZERO;
            for (int i = 0; i < len; i++)
            {
                int index;
                string id = dataFile.GetChromatogramId(i, out index);

                if (!ChromKey.IsKeyId(id))
                    continue;

                var chromKey = ChromKey.FromId(id, fixCEOptForShimadzu);
                if (chromKey.Precursor != lastPrecursor)
                {
                    lastPrecursor = chromKey.Precursor;
                    indexPrecursor++;
                }
                if (chromKey.Precursor.IsNegative)
                {
                    _sourceHasNegativePolarityData = true;
                }
                else
                {
                    _sourceHasPositivePolarityData = true;
                }
                var ki = new ChromKeyProviderIdPair(chromKey, index);
                _chromIndices[index] = indexPrecursor;
                _chromIds.Add(ki);
            }

            // Shimadzu can't do the necessary product m/z stepping for itself.
            // So, they provide the CE values in their IDs and we need to adjust
            // product m/z values for them to support CE optimization.
            if (fixCEOptForShimadzu)
                FixCEOptForShimadzu();

            if (_chromIds.Count == 0)
                throw new NoSrmDataException(FileInfo.FilePath);

            // CONSIDER: TIC and BPC are not well defined for SRM and produced chromatograms with over 100,000 points in
            // Agilent CE optimization data. So, keep them off for now.
//            foreach (int globalIndex in _globalChromatogramExtractor.GlobalChromatogramIndexes)
//            {
//                _chromIndices[globalIndex] = globalIndex;
//                _chromIds.Add(new ChromKeyProviderIdPair(ChromKey.FromId(_globalChromatogramExtractor.GetChromatogramId(globalIndex, out int indexId), false), globalIndex));
//            }

            foreach (var qcTracePair in _globalChromatogramExtractor.QcTraceByIndex)
            {
                _chromIndices[qcTracePair.Key] = qcTracePair.Key;
                _chromIds.Add(new ChromKeyProviderIdPair(ChromKey.FromQcTrace(qcTracePair.Value), qcTracePair.Key));
            }

            // CONSIDER(kaipot): Some way to support mzML files converted from MIDAS wiff files
            _hasMidasSpectra = (dataFile.IsABFile) && SpectraChromDataProvider.HasSpectrumData(dataFile);

            _hasSonarSpectra = dataFile.IsWatersSonarData();

            _ionMobilityUnits = dataFile.IonMobilityUnits;

            SetPercentComplete(50);
        }

        private void FixCEOptForShimadzu()
        {
            // Need to sort by keys to ensure everything is in the right order.
            _chromIds.Sort();

            int indexLast = 0;
            var lastPrecursor = SignedMz.ZERO;
            var lastProduct = SignedMz.ZERO;
            for (int i = 0; i < _chromIds.Count; i++)
            {
                var chromKey = _chromIds[i].Key;
                if (chromKey.Precursor != lastPrecursor || chromKey.Product != lastProduct)
                {
                    int count = i - indexLast;
                    if (HasConstantCEInterval(indexLast, count))
                    {
                        AddCEMzSteps(indexLast, count);
                    }
                    lastPrecursor = chromKey.Precursor;
                    lastProduct = chromKey.Product;
                    indexLast = i;
                }
            }
            int finalCount = _chromIds.Count - indexLast;
            if (HasConstantCEInterval(indexLast, finalCount))
            {
                AddCEMzSteps(indexLast, finalCount);
            }
        }

        private float GetCE(int i)
        {
            return _chromIds[i].Key.CollisionEnergy;
        }

        private bool HasConstantCEInterval(int start, int count)
        {
            // Need at least 3 steps for CE optimization
            if (count < 3)
                return false;

            double ceStart = GetCE(start);
            double ceEnd = GetCE(start + count - 1);
            double expectedInterval = (ceEnd - ceStart)/(count - 1);
            if (expectedInterval == 0)
                return false;

            for (int i = 1; i < count; i++)
            {
                double interval = GetCE(start + i) - GetCE(start + i - 1);
                if (Math.Abs(interval - expectedInterval) > 0.001)
                    return false;
            }
            return true;
        }

        private void AddCEMzSteps(int start, int count)
        {
            int step = count / 2;
            for (int i = count - 1; i >= 0; i--)
            {
                var chromId = _chromIds[start + i];
                var chromKeyNew = chromId.Key.ChangeOptimizationStep(step);
                _chromIds[start + i] = new ChromKeyProviderIdPair(chromKeyNew, chromId.ProviderId);
                step--;
            }
        }

        public override IEnumerable<ChromKeyProviderIdPair> ChromIds
        {
            get { return _chromIds; }
        }

        public override eIonMobilityUnits IonMobilityUnits { get { return _ionMobilityUnits; } }

        public override bool GetChromatogram(int id, Target modifiedSequence, Color color, out ChromExtra extra, out TimeIntensities timeIntensities)
        {
            float[] times, intensities;
            if (!_globalChromatogramExtractor.GetChromatogram(id, out times, out intensities))
            {
                _dataFile.GetChromatogram(id, out _, out times, out intensities);
            }

            timeIntensities = new TimeIntensities(times, intensities, null, null);

            // Assume that each chromatogram will be read once, though this may
            // not always be completely true.
            _readChromatograms++;

            if (_readChromatograms < _chromIds.Count)
                SetPercentComplete(50 + _readChromatograms * 50 / _chromIds.Count);

            int index = _chromIndices[id];
            extra = new ChromExtra(index, -1);  // TODO: is zero the right value?

            // Display in AllChromatogramsGraph
            var loadingStatus = Status as ChromatogramLoadingStatus;
            if (loadingStatus != null)
                loadingStatus.Transitions.AddTransition(
                    modifiedSequence,
                    color,
                    index, -1,
                    times,
                    intensities);
            return true;
        }

        public override double? MaxIntensity
        {
            get { return null; }
        }

        public override double? MaxRetentionTime
        {
            get { return null; }
        }

        public override bool IsProcessedScans
        {
            get { return false; }
        }

        public override bool IsSingleMzMatch
        {
            get { return false; }
        }

        public override bool IsSrm
        {
            get { return true; }
        }

        public override bool HasMidasSpectra
        {
            get { return _hasMidasSpectra; }
        }

        public override bool HasSonarSpectra
        {
            get { return _hasSonarSpectra; }
        }

        public override bool SourceHasPositivePolarityData
        {
            get { return _sourceHasPositivePolarityData; }
        }

        public override bool SourceHasNegativePolarityData
        {
            get { return _sourceHasNegativePolarityData; }
        }

        public static bool HasChromatogramData(MsDataFileImpl dataFile)
        {
            return dataFile.HasChromatogramData;
        }

        public override void ReleaseMemory()
        {
            Dispose();
        }

        public override void Dispose()
        {
            if (_dataFile != null)
                _dataFile.Dispose();
            _dataFile = null;
        }
    }
}
