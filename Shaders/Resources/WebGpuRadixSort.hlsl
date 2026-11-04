// SPDX-License-Identifier: MIT
// Subgroup-free 8-bit LSD Radix Sort for WebGPU and platforms without SM 6.0 wave intrinsics.
// Uses [numthreads(256, 1, 1)] and groupshared memory scans in uniform control flow.

#ifndef WEBGPU_RADIX_SORT_HLSL
#define WEBGPU_RADIX_SORT_HLSL

#define WG_SIZE     256U
#define RADIX_BINS  256U
#define RADIX_MASK  255U
#define SUB_TILES   15U
#define PART_SIZE   3840U // SUB_TILES * WG_SIZE (matches DEVICE_RADIX_SORT_PARTITION_SIZE)

uint e_numKeys;
uint e_radixShift;
uint e_threadBlocks;

RWStructuredBuffer<uint> b_sort;
RWStructuredBuffer<uint> b_alt;
RWStructuredBuffer<uint> b_sortPayload;
RWStructuredBuffer<uint> b_altPayload;
RWStructuredBuffer<uint> b_globalHist;
RWStructuredBuffer<uint> b_passHist;

groupshared uint g_wgHist[WG_SIZE];
groupshared uint g_wgScan[WG_SIZE];
groupshared uint2 g_wgPrefix2[WG_SIZE];
groupshared uint g_wgKey[WG_SIZE];
groupshared uint g_wgPayload[WG_SIZE];
groupshared uint g_wgDigitStart[WG_SIZE];
groupshared uint g_wgDigitEnd[WG_SIZE];

inline uint ExtractRadixDigit(uint key)
{
    return (key >> e_radixShift) & RADIX_MASK;
}

inline uint GlobalRadixHistOffset()
{
    return e_radixShift << 5; // (e_radixShift / 8) * 256
}

[numthreads(WG_SIZE, 1, 1)]
void InitWebGpuRadixSort(uint3 id : SV_DispatchThreadID)
{
    for (uint i = id.x; i < 1024U; i += WG_SIZE)
    {
        b_globalHist[i] = 0U;
    }
}

[numthreads(WG_SIZE, 1, 1)]
void UpsweepWebGpu(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint tid = gtid.x;
    uint groupIdx = gid.x;

    g_wgHist[tid] = 0U;
    GroupMemoryBarrierWithGroupSync();

    uint partStart = groupIdx * PART_SIZE;
    uint partEnd = min(partStart + PART_SIZE, e_numKeys);
    for (uint i = partStart + tid; i < partEnd; i += WG_SIZE)
    {
        uint digit = ExtractRadixDigit(b_sort[i]);
        InterlockedAdd(g_wgHist[digit], 1U);
    }
    GroupMemoryBarrierWithGroupSync();

    uint count = g_wgHist[tid];
    b_passHist[tid * e_threadBlocks + groupIdx] = count;

    // Inclusive Hillis-Steele prefix scan across the 256 digit counts
    g_wgScan[tid] = count;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint offset = 1U; offset < WG_SIZE; offset <<= 1U)
    {
        uint val = g_wgScan[tid];
        if (tid >= offset)
            val += g_wgScan[tid - offset];
        GroupMemoryBarrierWithGroupSync();
        g_wgScan[tid] = val;
        GroupMemoryBarrierWithGroupSync();
    }

    uint exc = (tid > 0U) ? g_wgScan[tid - 1U] : 0U;
    if (exc > 0U)
    {
        InterlockedAdd(b_globalHist[GlobalRadixHistOffset() + tid], exc);
    }
}

[numthreads(WG_SIZE, 1, 1)]
void ScanWebGpu(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint tid = gtid.x;
    uint digit = gid.x;
    uint baseOffset = digit * e_threadBlocks;
    uint reduction = 0U;

    for (uint baseIdx = 0U; baseIdx < e_threadBlocks; baseIdx += WG_SIZE)
    {
        uint idx = baseIdx + tid;
        uint val = (idx < e_threadBlocks) ? b_passHist[baseOffset + idx] : 0U;
        g_wgScan[tid] = val;
        GroupMemoryBarrierWithGroupSync();

        [unroll]
        for (uint offset = 1U; offset < WG_SIZE; offset <<= 1U)
        {
            uint sumVal = g_wgScan[tid];
            if (tid >= offset)
                sumVal += g_wgScan[tid - offset];
            GroupMemoryBarrierWithGroupSync();
            g_wgScan[tid] = sumVal;
            GroupMemoryBarrierWithGroupSync();
        }

        uint exc = (tid > 0U) ? g_wgScan[tid - 1U] : 0U;
        if (idx < e_threadBlocks)
        {
            b_passHist[baseOffset + idx] = reduction + exc;
        }
        reduction += g_wgScan[WG_SIZE - 1U];
        GroupMemoryBarrierWithGroupSync();
    }
}

[numthreads(WG_SIZE, 1, 1)]
void DownsweepWebGpu(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint tid = gtid.x;
    uint groupIdx = gid.x;

    // Load global base offset for each digit in this partition
    g_wgHist[tid] = b_globalHist[GlobalRadixHistOffset() + tid] + b_passHist[tid * e_threadBlocks + groupIdx];
    g_wgDigitStart[tid] = 0U;
    g_wgDigitEnd[tid] = 0U;
    GroupMemoryBarrierWithGroupSync();

    uint partStart = groupIdx * PART_SIZE;

    // Process 15 sub-tiles of 256 elements in uniform control flow
    for (uint subTile = 0U; subTile < SUB_TILES; ++subTile)
    {
        uint baseIdx = partStart + subTile * WG_SIZE;
        uint validCount = (baseIdx < e_numKeys) ? min(WG_SIZE, e_numKeys - baseIdx) : 0U;

        uint srcIdx = baseIdx + tid;
        bool isValid = tid < validCount;
        uint key = isValid ? b_sort[srcIdx] : 0xFFFFFFFFU;
        uint payload = isValid ? b_sortPayload[srcIdx] : 0xFFFFFFFFU;

        // 4 iterations of 2-bit (4-way) stable local radix sort over 256 elements
        [unroll]
        for (uint bitOffset = 0U; bitOffset < 8U; bitOffset += 2U)
        {
            uint shift = e_radixShift + bitOffset;
            uint bucket = (key >> shift) & 3U;

            // Pack 4 bucket counters into uint2 (16 bits per counter, max value 256)
            uint2 myCount = uint2(
                bucket == 0U ? 1U : (bucket == 1U ? 0x10000U : 0U),
                bucket == 2U ? 1U : (bucket == 3U ? 0x10000U : 0U)
            );
            g_wgPrefix2[tid] = myCount;
            GroupMemoryBarrierWithGroupSync();

            [unroll]
            for (uint offset = 1U; offset < WG_SIZE; offset <<= 1U)
            {
                uint2 val = g_wgPrefix2[tid];
                if (tid >= offset)
                    val += g_wgPrefix2[tid - offset];
                GroupMemoryBarrierWithGroupSync();
                g_wgPrefix2[tid] = val;
                GroupMemoryBarrierWithGroupSync();
            }

            uint2 totalPacked = g_wgPrefix2[WG_SIZE - 1U];
            uint total0 = totalPacked.x & 0xFFFFU;
            uint total1 = total0 + (totalPacked.x >> 16U);
            uint total2 = total1 + (totalPacked.y & 0xFFFFU);

            uint2 excPacked = (tid > 0U) ? g_wgPrefix2[tid - 1U] : uint2(0U, 0U);
            uint localDst;
            if (bucket == 0U)
                localDst = excPacked.x & 0xFFFFU;
            else if (bucket == 1U)
                localDst = total0 + (excPacked.x >> 16U);
            else if (bucket == 2U)
                localDst = total1 + (excPacked.y & 0xFFFFU);
            else
                localDst = total2 + (excPacked.y >> 16U);

            g_wgKey[localDst] = key;
            g_wgPayload[localDst] = payload;
            GroupMemoryBarrierWithGroupSync();

            key = g_wgKey[tid];
            payload = g_wgPayload[tid];
            GroupMemoryBarrierWithGroupSync();
        }

        // Now g_wgKey[0 .. validCount - 1] is stably sorted by the 8-bit digit
        uint digit = ExtractRadixDigit(key);
        if (tid < validCount)
        {
            if (tid == 0U || digit != ExtractRadixDigit(g_wgKey[tid - 1U]))
                g_wgDigitStart[digit] = tid;
            if (tid == validCount - 1U || digit != ExtractRadixDigit(g_wgKey[tid + 1U]))
                g_wgDigitEnd[digit] = tid + 1U;
        }
        GroupMemoryBarrierWithGroupSync();

        if (tid < validCount)
        {
            uint localRank = tid - g_wgDigitStart[digit];
            uint globalDst = g_wgHist[digit] + localRank;
            b_alt[globalDst] = key;
            b_altPayload[globalDst] = payload;
        }
        GroupMemoryBarrierWithGroupSync();

        // Advance per-digit global base offsets for the next sub-tile
        uint digitCount = g_wgDigitEnd[tid] - g_wgDigitStart[tid];
        g_wgHist[tid] += digitCount;
        g_wgDigitStart[tid] = 0U;
        g_wgDigitEnd[tid] = 0U;
        GroupMemoryBarrierWithGroupSync();
    }
}

#endif // WEBGPU_RADIX_SORT_HLSL
