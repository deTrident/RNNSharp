﻿using AdvUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

/// <summary>
/// RNNSharp written by Zhongkai Fu (fuzhongkai@gmail.com)
/// </summary>

namespace RNNSharp
{
    public class PAIR<T, K>
    {
        public T first;
        public K second;

        public PAIR(T f, K s)
        {
            first = f;
            second = s;
        }
    }

    public class ForwardRNN<T> : RNN<T> where T : ISequence
    {
        public ForwardRNN(List<SimpleLayer> hiddenLayerList, SimpleLayer outputLayer)
        {
            HiddenLayerList = hiddenLayerList;
            OutputLayer = outputLayer;
        }

        public ForwardRNN()
        {
        }

        public List<SimpleLayer> HiddenLayerList { get; set; }

        public override int GetTopHiddenLayerSize()
        {
            return HiddenLayerList[HiddenLayerList.Count - 1].LayerSize;
        }

        public override List<float[]> ComputeTopHiddenLayerOutput(Sequence pSequence)
        {
            var numStates = pSequence.States.Length;
            var numLayers = HiddenLayerList.Count;

            //reset all layers
            foreach (var layer in HiddenLayerList)
            {
                layer.Reset(false);
            }

            var outputs = new List<float[]>();
            for (var curState = 0; curState < numStates; curState++)
            {
                //Compute first layer
                var state = pSequence.States[curState];
                SetRuntimeFeatures(state, curState, numStates, null);
                HiddenLayerList[0].ForwardPass(state.SparseFeature, state.DenseFeature.CopyTo(), false);

                //Compute each layer
                for (var i = 1; i < numLayers; i++)
                {
                    //We use previous layer's output as dense feature for current layer
                    HiddenLayerList[i].ForwardPass(state.SparseFeature, HiddenLayerList[i - 1].Cell, false);
                }

                var tmpOutput = new float[HiddenLayerList[numLayers - 1].Cell.Length];
                for (var i = 0; i < HiddenLayerList[numLayers - 1].Cell.Length; i++)
                {
                    tmpOutput[i] = HiddenLayerList[numLayers - 1].Cell[i];
                }
                outputs.Add(tmpOutput);
            }

            return outputs;
        }

        /// <summary>
        /// Extract features from source sequence
        /// </summary>
        /// <param name="decoder"></param>
        /// <param name="srcSequence"></param>
        /// <param name="targetSparseFeatureSize"></param>
        /// <param name="srcHiddenAvgOutput"></param>
        /// <param name="srcSparseFeatures"></param>
        private void ExtractSourceSentenceFeature(RNNDecoder decoder, Sequence srcSequence, int targetSparseFeatureSize,
            out float[] srcHiddenAvgOutput, out Dictionary<int, float> srcSparseFeatures)
        {
            //Extract dense features from source sequence
            var srcOutputs = decoder.ComputeTopHiddenLayerOutput(srcSequence);
            int srcSequenceDenseFeatureSize = srcOutputs[0].Length;
            int srcSequenceLength = srcOutputs.Count - 1;
            srcHiddenAvgOutput = new float[srcSequenceDenseFeatureSize];

            var j = 0;
            var vDiv2 = new Vector<float>(2.0f);
            float[] srcOutputForward = srcOutputs[0];
            float[] srcOutputBackward = srcOutputs[srcSequenceLength];
            while (j < srcSequenceDenseFeatureSize - Vector<float>.Count)
            {
                var vForward = new Vector<float>(srcOutputForward, j);
                var vBackward = new Vector<float>(srcOutputBackward, j);

                var vResult = (vForward + vBackward) / vDiv2;
                vResult.CopyTo(srcHiddenAvgOutput, j);

                j += Vector<float>.Count;
            }

            while (j < srcSequenceDenseFeatureSize)
            {
                srcHiddenAvgOutput[j] = (srcOutputForward[j] + srcOutputBackward[j]) / 2.0f;
                j++;
            }

            //Extract sparse features from source sequence
            srcSparseFeatures = new Dictionary<int, float>();
            for (var i = 0; i < srcSequence.States.Length; i++)
            {
                foreach (var kv in srcSequence.States[i].SparseFeature)
                {
                    var srcSparseFeatureIndex = kv.Key + targetSparseFeatureSize;

                    if (srcSparseFeatures.ContainsKey(srcSparseFeatureIndex) == false)
                    {
                        srcSparseFeatures.Add(srcSparseFeatureIndex, kv.Value);
                    }
                    else
                    {
                        srcSparseFeatures[srcSparseFeatureIndex] += kv.Value;
                    }
                }
            }
        }

        public override int[] TestSeq2Seq(Sentence srcSentence, Config featurizer)
        {
            var curState = featurizer.BuildState(new[] { "<s>" });
            curState.Label = featurizer.TagSet.GetIndex("<s>");

            //Reset all layers
            foreach (var layer in HiddenLayerList)
            {
                layer.Reset(false);
            }

            //Extract features from source sentence
            var srcSequence = featurizer.Seq2SeqAutoEncoder.Config.BuildSequence(srcSentence);
            float[] srcHiddenAvgOutput;
            Dictionary<int, float> srcSparseFeatures;
            ExtractSourceSentenceFeature(featurizer.Seq2SeqAutoEncoder, srcSequence, curState.SparseFeature.Length,
                out srcHiddenAvgOutput, out srcSparseFeatures);

            var numLayers = HiddenLayerList.Count;
            var predicted = new List<int> { curState.Label };
            while (true)
            {
                //Build sparse features
                var sparseVector = new SparseVector();
                sparseVector.SetLength(curState.SparseFeature.Length + srcSequence.SparseFeatureSize);
                sparseVector.AddKeyValuePairData(curState.SparseFeature);
                sparseVector.AddKeyValuePairData(srcSparseFeatures);

                //Compute first layer
                var denseFeatures = RNNHelper.ConcatenateVector(curState.DenseFeature, srcHiddenAvgOutput);
                HiddenLayerList[0].ForwardPass(sparseVector, denseFeatures, false);

                //Compute middle layers
                for (var i = 1; i < numLayers; i++)
                {
                    //We use previous layer's output as dense feature for current layer
                    denseFeatures = RNNHelper.ConcatenateVector(HiddenLayerList[i - 1].Cell, srcHiddenAvgOutput);
                    HiddenLayerList[i].ForwardPass(sparseVector, denseFeatures, false);
                }

                //Compute output layer
                denseFeatures = RNNHelper.ConcatenateVector(HiddenLayerList[numLayers - 1].Cell,
                    srcHiddenAvgOutput);
                OutputLayer.ForwardPass(sparseVector, denseFeatures, false);

                OutputLayer.Softmax(false);

                var nextTagId = OutputLayer.GetBestOutputIndex(false);
                var nextWord = featurizer.TagSet.GetTagName(nextTagId);

                curState = featurizer.BuildState(new[] { nextWord });
                curState.Label = nextTagId;

                predicted.Add(nextTagId);

                if (nextWord == "</s>" || predicted.Count >= 100)
                {
                    break;
                }
            }

            return predicted.ToArray();
        }

    
        public override int[] ProcessSeq2Seq(SequencePair pSequence, RunningMode runningMode)
        {
            var tgtSequence = pSequence.tgtSequence;
            var isTraining = runningMode == RunningMode.Training;

            //Reset all layers
            foreach (var layer in HiddenLayerList)
            {
                layer.Reset(isTraining);
            }

            //Extract features from source sentences
            var srcSequence = pSequence.autoEncoder.Config.BuildSequence(pSequence.srcSentence);
            float[] srcHiddenAvgOutput;
            Dictionary<int, float> srcSparseFeatures;
            ExtractSourceSentenceFeature(pSequence.autoEncoder, srcSequence, tgtSequence.SparseFeatureSize,
                out srcHiddenAvgOutput, out srcSparseFeatures);

            var numStates = pSequence.tgtSequence.States.Length;
            var numLayers = HiddenLayerList.Count;
            var predicted = new int[numStates];

            //Set target sentence labels into short list in output layer
            OutputLayer.LabelShortList = new List<int>();
            foreach (var state in tgtSequence.States)
            {
                OutputLayer.LabelShortList.Add(state.Label);
            }

            for (var curState = 0; curState < numStates; curState++)
            {
                //Build runtime features
                var state = tgtSequence.States[curState];
                SetRuntimeFeatures(state, curState, numStates, predicted);

                //Build sparse features for all layers
                var sparseVector = new SparseVector();
                sparseVector.SetLength(tgtSequence.SparseFeatureSize + srcSequence.SparseFeatureSize);
                sparseVector.AddKeyValuePairData(state.SparseFeature);
                sparseVector.AddKeyValuePairData(srcSparseFeatures);

                //Compute first layer
                var denseFeatures = RNNHelper.ConcatenateVector(state.DenseFeature, srcHiddenAvgOutput);
                HiddenLayerList[0].ForwardPass(sparseVector, denseFeatures, isTraining);

                //Compute middle layers
                for (var i = 1; i < numLayers; i++)
                {
                    //We use previous layer's output as dense feature for current layer
                    denseFeatures = RNNHelper.ConcatenateVector(HiddenLayerList[i - 1].Cell, srcHiddenAvgOutput);
                    HiddenLayerList[i].ForwardPass(sparseVector, denseFeatures, isTraining);
                }

                //Compute output layer
                denseFeatures = RNNHelper.ConcatenateVector(HiddenLayerList[numLayers - 1].Cell,
                    srcHiddenAvgOutput);
                OutputLayer.ForwardPass(sparseVector, denseFeatures, isTraining);

                OutputLayer.Softmax(isTraining);

                predicted[curState] = OutputLayer.GetBestOutputIndex(isTraining);

                if (runningMode != RunningMode.Test)
                {
                    logp += Math.Log10(OutputLayer.Cell[state.Label] + 0.0001);
                }

                if (runningMode == RunningMode.Training)
                {
                    // error propogation
                    OutputLayer.ComputeLayerErr(CRFSeqOutput, state, curState);

                    //propogate errors to each layer from output layer to input layer
                    HiddenLayerList[numLayers - 1].ComputeLayerErr(OutputLayer);
                    for (var i = numLayers - 2; i >= 0; i--)
                    {
                        HiddenLayerList[i].ComputeLayerErr(HiddenLayerList[i + 1]);
                    }

                    //Update net weights
                    Parallel.Invoke(() => { OutputLayer.BackwardPass(numStates, curState); },
                        () =>
                        {
                            Parallel.For(0, numLayers, parallelOption,
                                i => { HiddenLayerList[i].BackwardPass(numStates, curState); });
                        });
                }
            }

            return predicted;
        }

        public override int[] ProcessSequence(Sequence pSequence, RunningMode runningMode, bool outputRawScore,
            out Matrix<float> m)
        {
            var numStates = pSequence.States.Length;
            var numLayers = HiddenLayerList.Count;

            m = outputRawScore ? new Matrix<float>(numStates, OutputLayer.LayerSize) : null;

            var predicted = new int[numStates];
            var isTraining = runningMode == RunningMode.Training;

            //reset all layers
            foreach (var layer in HiddenLayerList)
            {
                layer.Reset(isTraining);
            }

            //Set current sentence labels into short list in output layer
            OutputLayer.LabelShortList = new List<int>();
            foreach (var state in pSequence.States)
            {
                OutputLayer.LabelShortList.Add(state.Label);
            }

            for (var curState = 0; curState < numStates; curState++)
            {
                //Compute first layer
                var state = pSequence.States[curState];
                SetRuntimeFeatures(state, curState, numStates, predicted);
                HiddenLayerList[0].ForwardPass(state.SparseFeature, state.DenseFeature.CopyTo(), isTraining);

                //Compute each layer
                for (var i = 1; i < numLayers; i++)
                {
                    //We use previous layer's output as dense feature for current layer
                    HiddenLayerList[i].ForwardPass(state.SparseFeature, HiddenLayerList[i - 1].Cell, isTraining);
                }

                //Compute output layer
                OutputLayer.ForwardPass(state.SparseFeature, HiddenLayerList[numLayers - 1].Cell, isTraining);

                if (m != null)
                {
                    OutputLayer.Cell.CopyTo(m[curState], 0);
                }

                OutputLayer.Softmax(isTraining);

                predicted[curState] = OutputLayer.GetBestOutputIndex(isTraining);

                if (runningMode != RunningMode.Test)
                {
                    logp += Math.Log10(OutputLayer.Cell[state.Label] + 0.0001);
                }

                if (runningMode == RunningMode.Training)
                {
                    // error propogation
                    OutputLayer.ComputeLayerErr(CRFSeqOutput, state, curState);

                    //propogate errors to each layer from output layer to input layer
                    HiddenLayerList[numLayers - 1].ComputeLayerErr(OutputLayer);
                    for (var i = numLayers - 2; i >= 0; i--)
                    {
                        HiddenLayerList[i].ComputeLayerErr(HiddenLayerList[i + 1]);
                    }

                    //Update net weights
                    Parallel.Invoke(() => { OutputLayer.BackwardPass(numStates, curState); },
                        () =>
                        {
                            Parallel.For(0, numLayers, parallelOption,
                                i => { HiddenLayerList[i].BackwardPass(numStates, curState); });
                        });
                }
            }

            return predicted;
        }

        public override int[] ProcessSequenceCRF(Sequence pSequence, RunningMode runningMode)
        {
            var numStates = pSequence.States.Length;
            var numLayers = HiddenLayerList.Count;

            //Get network output without CRF
            Matrix<float> nnOutput;
            ProcessSequence(pSequence, RunningMode.Test, true, out nnOutput);

            //Compute CRF result
            ForwardBackward(numStates, nnOutput);

            if (runningMode != RunningMode.Test)
            {
                //Get the best result
                for (var i = 0; i < numStates; i++)
                {
                    logp += Math.Log10(CRFSeqOutput[i][pSequence.States[i].Label] + 0.0001);
                }
            }

            //Compute best path in CRF result
            var predicted = Viterbi(nnOutput, numStates);

            if (runningMode == RunningMode.Training)
            {
                //Update tag bigram transition for CRF model
                UpdateBigramTransition(pSequence);

                //Reset all layer states
                foreach (var layer in HiddenLayerList)
                {
                    layer.Reset(true);
                }

                for (var curState = 0; curState < numStates; curState++)
                {
                    // error propogation
                    var state = pSequence.States[curState];
                    SetRuntimeFeatures(state, curState, numStates, null);
                    HiddenLayerList[0].ForwardPass(state.SparseFeature, state.DenseFeature.CopyTo());

                    for (var i = 1; i < numLayers; i++)
                    {
                        HiddenLayerList[i].ForwardPass(state.SparseFeature, HiddenLayerList[i - 1].Cell);
                    }

                    OutputLayer.ComputeLayerErr(CRFSeqOutput, state, curState);

                    HiddenLayerList[numLayers - 1].ComputeLayerErr(OutputLayer);
                    for (var i = numLayers - 2; i >= 0; i--)
                    {
                        HiddenLayerList[i].ComputeLayerErr(HiddenLayerList[i + 1]);
                    }

                    //Update net weights
                    Parallel.Invoke(() => { OutputLayer.BackwardPass(numStates, curState); },
                        () =>
                        {
                            Parallel.For(0, numLayers, parallelOption,
                                i => { HiddenLayerList[i].BackwardPass(numStates, curState); });
                        });
                }
            }

            return predicted;
        }

        // save model as binary format
        public override void SaveModel(string filename)
        {
            var sw = new StreamWriter(filename);
            var fo = new BinaryWriter(sw.BaseStream);

            if (HiddenLayerList[0] is BPTTLayer)
            {
                fo.Write((int)LAYERTYPE.BPTT);
            }
            else
            {
                fo.Write((int)LAYERTYPE.LSTM);
            }

            fo.Write(IsCRFTraining);

            fo.Write(HiddenLayerList.Count);
            foreach (var layer in HiddenLayerList)
            {
                layer.Save(fo);
            }
            OutputLayer.Save(fo);

            if (IsCRFTraining)
            {
                //Save CRF feature weights
                RNNHelper.SaveMatrix(CRFTagTransWeights, fo);
            }

            fo.Close();
        }

        public override void LoadModel(string filename)
        {
            Logger.WriteLine("Loading SimpleRNN model: {0}", filename);

            var sr = new StreamReader(filename);
            var br = new BinaryReader(sr.BaseStream);

            var layerType = (LAYERTYPE)br.ReadInt32();
            IsCRFTraining = br.ReadBoolean();

            //Create cells of each layer
            var layerSize = br.ReadInt32();
            HiddenLayerList = new List<SimpleLayer>();
            for (var i = 0; i < layerSize; i++)
            {
                SimpleLayer layer;
                if (layerType == LAYERTYPE.BPTT)
                {
                    layer = new BPTTLayer();
                }
                else
                {
                    layer = new LSTMLayer();
                }

                layer.Load(br);
                HiddenLayerList.Add(layer);
            }

            Logger.WriteLine("Create output layer");
            OutputLayer = new SimpleLayer();
            OutputLayer.Load(br);

            if (IsCRFTraining)
            {
                Logger.WriteLine("Loading CRF tag trans weights...");
                CRFTagTransWeights = RNNHelper.LoadMatrix(br);
            }

            sr.Close();
        }

        public override void CleanStatus()
        {
            foreach (var layer in HiddenLayerList)
            {
                layer.CleanLearningRate();
            }

            OutputLayer.CleanLearningRate();
        }
    }
}