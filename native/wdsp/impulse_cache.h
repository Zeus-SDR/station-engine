/*  impulse_cache.h

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2013, 2019, 2024 Warren Pratt, NR0V
Copyright (C) 2025 Richard Samphire, MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

The author can be reached by email at

warren@wpratt.com
mw0lge@grange-lane.co.uk

*/
//
//============================================================================================//
// Dual-Licensing Statement (Applies Only to Author's Contributions, Richard Samphire MW0LGE) //
// ------------------------------------------------------------------------------------------ //
// For any code originally written by Richard Samphire MW0LGE, or for any modifications     //
// made by him, the copyright holder for those portions (Richard Samphire) reserves the     //
// right to use, license, and distribute such code under different terms, including       //
// closed-source and proprietary licences, in addition to the GNU General Public License    //
// granted above. Nothing in this statement restricts any rights granted to recipients under  //
// the GNU GPL. Code contributed by others (not Richard Samphire) remains licensed under    //
// its original terms and is not affected by this dual-licensing statement in any way.      //
// Richard Samphire can be reached by email at :  mw0lge@grange-lane.co.uk            //
//============================================================================================//

#ifndef _impulse_cache_h
#define _impulse_cache_h

#include <stdint.h>
#include <stddef.h>

extern uint64_t fnv1a_hash64(const void* data, size_t len);
#define HASH_T       uint64_t
#define fnv1a_hash   fnv1a_hash64
#define GOLDEN_RATIO 0x9E3779B97F4A7C15ULL

#define MAX_CACHE_ENTRIES   4096  // secondary guard; byte budget is authoritative
#define MAX_CACHE_BYTES (64ULL * 1024ULL * 1024ULL)
#define CACHE_BUCKETS     4   // 4 cache buckets, for fir_bandpass, mp, eq, fc. Unique indexes in the #defines below

#define FIR_CACHE 0
#define MP_CACHE  1
#define EQ_CACHE  2
#define FC_CACHE  3

double* get_impulse_cache_entry(size_t bucket, HASH_T hash, int N);
void add_impulse_to_cache(size_t bucket, HASH_T hash, int N, double* impulse);
void ensure_impulse_cache_initialized(void);
void lock_mp_generation(void);
void unlock_mp_generation(void);

__declspec (dllexport) int save_impulse_cache(const char* path);
__declspec (dllexport) int read_impulse_cache(const char* path);
__declspec (dllexport) void use_impulse_cache(int use);

__declspec (dllexport) void init_impulse_cache(int use);
__declspec (dllexport) void destroy_impulse_cache(void);

#endif
