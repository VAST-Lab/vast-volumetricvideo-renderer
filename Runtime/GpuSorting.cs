// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Supports both Desktop SM 6.0 wave-intrinsic DeviceRadixSort (Thomas Smith 2024, MIT license)
    // and Subgroup-Free WebGPU Radix Sort (WebGpuRadixSort.hlsl).

    public class GpuSorting
    {
        //The size of a threadblock partition in the sort (both Wave and WebGPU sort use 3840)
        const uint DEVICE_RADIX_SORT_PARTITION_SIZE = 3840;

        //The size of our radix in bits
        const uint DEVICE_RADIX_SORT_BITS = 8;

        //Number of digits in our radix, 1 << DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_RADIX = 256;

        //Number of sorting passes required to sort a 32bit key, KEY_BITS / DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_PASSES = 4;

        public struct Args
        {
            public uint             count;
            public GraphicsBuffer   inputKeys;
            public GraphicsBuffer   inputValues;
            public SupportResources resources;
            internal int workGroupCount;
        }

        public struct SupportResources
        {
            public GraphicsBuffer altBuffer;
            public GraphicsBuffer altPayloadBuffer;
            public GraphicsBuffer passHistBuffer;
            public GraphicsBuffer globalHistBuffer;

            public static SupportResources Load(uint count)
            {
                //This is threadBlocks * DEVICE_RADIX_SORT_RADIX
                uint scratchBufferSize = DivRoundUp(count, DEVICE_RADIX_SORT_PARTITION_SIZE) * DEVICE_RADIX_SORT_RADIX; 
                uint reducedScratchBufferSize = DEVICE_RADIX_SORT_RADIX * DEVICE_RADIX_SORT_PASSES;

                var target = GraphicsBuffer.Target.Structured;
                var resources = new SupportResources();
                try
                {
                    resources.altBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "DeviceRadixAlt" };
                    resources.altPayloadBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "DeviceRadixAltPayload" };
                    resources.passHistBuffer = new GraphicsBuffer(target, (int)scratchBufferSize, 4) { name = "DeviceRadixPassHistogram" };
                    resources.globalHistBuffer = new GraphicsBuffer(target, (int)reducedScratchBufferSize, 4) { name = "DeviceRadixGlobalHistogram" };
                    return resources;
                }
                catch
                {
                    resources.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                altBuffer?.Dispose();
                altPayloadBuffer?.Dispose();
                passHistBuffer?.Dispose();
                globalHistBuffer?.Dispose();

                altBuffer = null;
                altPayloadBuffer = null;
                passHistBuffer = null;
                globalHistBuffer = null;
            }
        }

        readonly ComputeShader m_CS;
        readonly int m_kernelInitDeviceRadixSort = -1;
        readonly int m_kernelUpsweep = -1;
        readonly int m_kernelScan = -1;
        readonly int m_kernelDownsweep = -1;
        readonly bool m_UsingWebGpuSort;
        readonly bool m_Valid;

        public bool Valid => m_Valid;
        public bool UsingWebGpuSort => m_UsingWebGpuSort;

        public GpuSorting(ComputeShader csFallback, ComputeShader csWave = null)
        {
            bool useWave = false;
            if (csWave != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.WebGPU)
            {
                int kInit = csWave.HasKernel("InitDeviceRadixSort") ? csWave.FindKernel("InitDeviceRadixSort") : -1;
                int kUp = csWave.HasKernel("Upsweep") ? csWave.FindKernel("Upsweep") : -1;
                int kScan = csWave.HasKernel("Scan") ? csWave.FindKernel("Scan") : -1;
                int kDown = csWave.HasKernel("Downsweep") ? csWave.FindKernel("Downsweep") : -1;

                if (kInit >= 0 && kUp >= 0 && kScan >= 0 && kDown >= 0 &&
                    csWave.IsSupported(kInit) && csWave.IsSupported(kUp) &&
                    csWave.IsSupported(kScan) && csWave.IsSupported(kDown))
                {
                    m_CS = csWave;
                    m_kernelInitDeviceRadixSort = kInit;
                    m_kernelUpsweep = kUp;
                    m_kernelScan = kScan;
                    m_kernelDownsweep = kDown;
                    m_Valid = true;
                    m_UsingWebGpuSort = false;
                    useWave = true;

                    csWave.EnableKeyword(new LocalKeyword(csWave, "KEY_UINT"));
                    csWave.EnableKeyword(new LocalKeyword(csWave, "PAYLOAD_UINT"));
                    csWave.EnableKeyword(new LocalKeyword(csWave, "SHOULD_ASCEND"));
                    csWave.EnableKeyword(new LocalKeyword(csWave, "SORT_PAIRS"));
                    var vulkanKw = new LocalKeyword(csWave, "VULKAN");
                    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
                        csWave.EnableKeyword(vulkanKw);
                    else
                        csWave.DisableKeyword(vulkanKw);
                }
            }

            if (!useWave && csFallback != null)
            {
                m_CS = csFallback;
                m_UsingWebGpuSort = true;
                m_kernelInitDeviceRadixSort = csFallback.HasKernel("InitWebGpuRadixSort") ? csFallback.FindKernel("InitWebGpuRadixSort") : -1;
                m_kernelUpsweep = csFallback.HasKernel("UpsweepWebGpu") ? csFallback.FindKernel("UpsweepWebGpu") : -1;
                m_kernelScan = csFallback.HasKernel("ScanWebGpu") ? csFallback.FindKernel("ScanWebGpu") : -1;
                m_kernelDownsweep = csFallback.HasKernel("DownsweepWebGpu") ? csFallback.FindKernel("DownsweepWebGpu") : -1;

                m_Valid = m_kernelInitDeviceRadixSort >= 0 &&
                          m_kernelUpsweep >= 0 &&
                          m_kernelScan >= 0 &&
                          m_kernelDownsweep >= 0 &&
                          csFallback.IsSupported(m_kernelInitDeviceRadixSort) &&
                          csFallback.IsSupported(m_kernelUpsweep) &&
                          csFallback.IsSupported(m_kernelScan) &&
                          csFallback.IsSupported(m_kernelDownsweep);
            }
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        struct SortConstants
        {
            public uint numKeys;                        // The number of keys to sort
            public uint radixShift;                     // The radix shift value for the current pass
            public uint threadBlocks;                   // threadBlocks
            public uint padding0;                       // Padding - unused
        }

        public void Dispatch(CommandBuffer cmd, Args args)
        {
            Assert.IsTrue(Valid);

            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.altBuffer;
            GraphicsBuffer dstPayloadBuffer = args.resources.altPayloadBuffer;

            SortConstants constants = default;
            constants.numKeys = args.count;
            constants.threadBlocks = DivRoundUp(args.count, DEVICE_RADIX_SORT_PARTITION_SIZE);

            // Setup overall constants
            cmd.SetComputeIntParam(m_CS, "e_numKeys", (int)constants.numKeys);
            cmd.SetComputeIntParam(m_CS, "e_threadBlocks", (int)constants.threadBlocks);

            //Set statically located buffers
            //Upsweep
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_globalHist", args.resources.globalHistBuffer);

            //Scan
            cmd.SetComputeBufferParam(m_CS, m_kernelScan, "b_passHist", args.resources.passHistBuffer);

            //Downsweep
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_globalHist", args.resources.globalHistBuffer);

            //Clear the global histogram
            cmd.SetComputeBufferParam(m_CS, m_kernelInitDeviceRadixSort, "b_globalHist", args.resources.globalHistBuffer);
            cmd.DispatchCompute(m_CS, m_kernelInitDeviceRadixSort, 1, 1, 1);

            // Execute the sort algorithm in 8-bit increments
            for (constants.radixShift = 0; constants.radixShift < 32; constants.radixShift += DEVICE_RADIX_SORT_BITS)
            {
                cmd.SetComputeIntParam(m_CS, "e_radixShift", (int)constants.radixShift);

                //Upsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_sort", srcKeyBuffer);
                cmd.DispatchCompute(m_CS, m_kernelUpsweep, (int)constants.threadBlocks, 1, 1);

                // Scan
                cmd.DispatchCompute(m_CS, m_kernelScan, (int)DEVICE_RADIX_SORT_RADIX, 1, 1);

                // Downsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sort", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortPayload", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_alt", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_altPayload", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_kernelDownsweep, (int)constants.threadBlocks, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }
}
