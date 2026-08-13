/*  impulse_cache.c

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

#define _CRT_SECURE_NO_WARNINGS
#include "comm.h"

/********************************************************************************************************
*                                                   *
*               Impulse Cache implementation                      *
*                                                   *
********************************************************************************************************/

static const uint64_t FNV_OFFSET_BASIS_64 = 14695981039346656037ULL;  // 0xcbf29ce484222325
static const uint64_t FNV_PRIME_64 = 1099511628211ULL;          // 0x100000001b3

uint64_t fnv1a_hash64(const void* data, size_t len) {
  const uint8_t* bytes = (const uint8_t*)data;
  uint64_t hash = FNV_OFFSET_BASIS_64;

  for (size_t i = 0; i < len; ++i) {
    hash ^= bytes[i];
    hash *= FNV_PRIME_64;
  }

  return hash;
}

typedef struct _cache_entry {
  HASH_T  hash;
  int   N;              // N complex entries in impulse. Leave as signed int as that is used everywhere
  double* impulse;
  struct _cache_entry* next;
} cache_entry;

static size_t _cache_counts[CACHE_BUCKETS] = { 0 };
static size_t _cache_bytes[CACHE_BUCKETS] = { 0 };
static cache_entry* _cache_heads[CACHE_BUCKETS] = { NULL };
static CRITICAL_SECTION _cs_use_cache;
static CRITICAL_SECTION _cs_mp_generation;
static CRITICAL_SECTION _cs_cache;
static volatile LONG _init_state = 0;
static int _use_cache = 1;

static void remove_impulse_cache_tail_unlocked(size_t bucket) {
  if (bucket >= CACHE_BUCKETS) { return; }

  cache_entry** pp = &_cache_heads[bucket];

  while (*pp && (*pp)->next) {
    pp = &(*pp)->next;
  }

  if (*pp) {
    _cache_bytes[bucket] -= (size_t)(*pp)->N * sizeof(complex);
    _aligned_free((*pp)->impulse);
    _aligned_free(*pp);
    *pp = NULL;
    _cache_counts[bucket]--;
  }
}

static void free_impulse_cache_unlocked(void) {
  for (size_t b = 0; b < CACHE_BUCKETS; ++b) {
    cache_entry* e = _cache_heads[b];

    while (e) {
      cache_entry* next = e->next;
      _aligned_free(e->impulse);
      _aligned_free(e);
      e = next;
    }

    _cache_heads[b] = NULL;
    _cache_counts[b] = 0;
    _cache_bytes[b] = 0;
  }
}

void ensure_impulse_cache_initialized(void) {
  if (InterlockedCompareExchange(&_init_state, 2, 2) == 2) { return; }

  if (InterlockedCompareExchange(&_init_state, 1, 0) == 0) {
    InitializeCriticalSectionAndSpinCount(&_cs_use_cache, 2500);
    InitializeCriticalSectionAndSpinCount(&_cs_mp_generation, 2500);
    InitializeCriticalSectionAndSpinCount(&_cs_cache, 2500);
    _use_cache = 1;
    InterlockedExchange(&_init_state, 2);
    return;
  }

  while (InterlockedCompareExchange(&_init_state, 2, 2) != 2) { Sleep(1); }
}

void free_impulse_cache(void) {
  ensure_impulse_cache_initialized();
  EnterCriticalSection(&_cs_cache);
  free_impulse_cache_unlocked();
  LeaveCriticalSection(&_cs_cache);
}

double* get_impulse_cache_entry(size_t bucket, HASH_T hash, int N) {
  ensure_impulse_cache_initialized();
  int use;
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);

  if (!use || bucket >= CACHE_BUCKETS) { return NULL; }

  EnterCriticalSection(&_cs_cache);
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);
  if (!use) { LeaveCriticalSection(&_cs_cache); return NULL; }
  // lru, least recently used, moves cache hit to head
  // old cache entries will move towards the tail and eventually be dumped
  cache_entry* prev = NULL;
  cache_entry* e = _cache_heads[bucket];

  while (e) {
    if (e->hash == hash && e->N == N) {
      if (prev) {
        prev->next = e->next;
        e->next = _cache_heads[bucket];
        _cache_heads[bucket] = e;
      }

      double* imp = (double*) malloc0(e->N * sizeof(complex));
      memcpy(imp, e->impulse, e->N * sizeof(complex));
      LeaveCriticalSection(&_cs_cache);
      return imp;
    }

    prev = e;
    e = e->next;
  }

  LeaveCriticalSection(&_cs_cache);
  return NULL;
}

void add_impulse_to_cache(size_t bucket, HASH_T hash, int N, double* impulse) {
  ensure_impulse_cache_initialized();
  int use;
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);

  if (!use || bucket >= CACHE_BUCKETS) { return; }

  EnterCriticalSection(&_cs_cache);
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);
  if (!use) { LeaveCriticalSection(&_cs_cache); return; }
  size_t entry_bytes = (size_t)N * sizeof(complex);
  while (_cache_counts[bucket] > 0 &&
    (_cache_counts[bucket] >= MAX_CACHE_ENTRIES || _cache_bytes[bucket] + entry_bytes > MAX_CACHE_BYTES))
    remove_impulse_cache_tail_unlocked(bucket);

  if (entry_bytes > MAX_CACHE_BYTES) {
    LeaveCriticalSection(&_cs_cache);
    return;
  }

  cache_entry* e = malloc0(sizeof(cache_entry));
  e->hash = hash;
  e->N = N;
  e->impulse = (double *) malloc0(N * sizeof(complex));
  memcpy(e->impulse, impulse, N * sizeof(complex));
  e->next = _cache_heads[bucket];
  _cache_heads[bucket] = e;
  _cache_counts[bucket]++;
  _cache_bytes[bucket] += entry_bytes;
  LeaveCriticalSection(&_cs_cache);
}

void lock_mp_generation(void) {
  ensure_impulse_cache_initialized();
  EnterCriticalSection(&_cs_mp_generation);
}
void unlock_mp_generation(void) { LeaveCriticalSection(&_cs_mp_generation); }

PORT
int save_impulse_cache(const char* path) {
  ensure_impulse_cache_initialized();
  int use;
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);

  if (!use) { return 0; }

  FILE* fp = fopen(path, "wb");

  if (!fp) { return -1; }

  EnterCriticalSection(&_cs_cache);
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);
  if (!use) { LeaveCriticalSection(&_cs_cache); fclose(fp); return 0; }

  const uint32_t magic = 0x5A464952U; // "ZFIR"
  const uint32_t version = 2U;       // v2 standardizes 64-bit hashes on every OS
  uint32_t buckets = CACHE_BUCKETS;

  if (fwrite(&magic, sizeof(magic), 1, fp) != 1
    || fwrite(&version, sizeof(version), 1, fp) != 1
    || fwrite(&buckets, sizeof(buckets), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

  for (size_t b = 0; b < CACHE_BUCKETS; b++) {
    uint32_t count = 0;

    for (cache_entry * e = _cache_heads[b]; e; e = e->next) { count++; }

    if (fwrite(&count, sizeof(count), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

    for (cache_entry * e = _cache_heads[b]; e; e = e->next) {
      if (fwrite(&e->hash, sizeof(HASH_T), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

      if (fwrite(&e->N, sizeof(e->N), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

      if (fwrite(e->impulse, sizeof(complex), e->N, fp) != (size_t)e->N) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }
    }
  }

  LeaveCriticalSection(&_cs_cache);
  fclose(fp);
  return 0;
}

PORT
int read_impulse_cache(const char* path) {
  ensure_impulse_cache_initialized();
  int use;
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);

  if (!use) { return 0; }

  FILE* fp = fopen(path, "rb");

  if (!fp) { return -1; }

  EnterCriticalSection(&_cs_cache);
  EnterCriticalSection(&_cs_use_cache);
  use = _use_cache;
  LeaveCriticalSection(&_cs_use_cache);
  if (!use) { LeaveCriticalSection(&_cs_cache); fclose(fp); return 0; }
  free_impulse_cache_unlocked();

  uint32_t magic;
  uint32_t version;
  uint32_t buckets;

  if (fread(&magic, sizeof(magic), 1, fp) != 1
    || fread(&version, sizeof(version), 1, fp) != 1
    || fread(&buckets, sizeof(buckets), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

  if (magic != 0x5A464952U || version != 2U || buckets != CACHE_BUCKETS) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

  for (size_t b = 0; b < buckets; b++) {
    uint32_t count;

    if (fread(&count, sizeof(count), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

    cache_entry* tail = NULL;

    for (uint32_t i = 0; i < count; i++) {
      HASH_T hash;
      int    N;

      if (fread(&hash, sizeof(HASH_T), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

      if (fread(&N, sizeof(N), 1, fp) != 1) { LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

      double* data = (double*)malloc0(N * sizeof(complex));

      if (fread(data, sizeof(complex), N, fp) != (size_t)N) { _aligned_free(data); LeaveCriticalSection(&_cs_cache); fclose(fp); return -1; }

      cache_entry* e = (cache_entry*)malloc0(sizeof(cache_entry));
      e->hash = hash;
      e->N = N;
      e->impulse = data;
      e->next = NULL;

      size_t entry_bytes = (size_t)N * sizeof(complex);
      if (_cache_counts[b] >= MAX_CACHE_ENTRIES || _cache_bytes[b] + entry_bytes > MAX_CACHE_BYTES) {
        _aligned_free(data);
        _aligned_free(e);
        continue;
      }

      if (tail) {
        tail->next = e;
      } else {
        _cache_heads[b] = e;
      }

      tail = e;
      _cache_counts[b]++;
      _cache_bytes[b] += entry_bytes;
    }
  }

  LeaveCriticalSection(&_cs_cache);
  fclose(fp);
  return 0;
}

PORT
void use_impulse_cache(int use) {
  ensure_impulse_cache_initialized();
  EnterCriticalSection(&_cs_use_cache);
  _use_cache = use;
  LeaveCriticalSection(&_cs_use_cache);
}

PORT
void init_impulse_cache(int use) {
  ensure_impulse_cache_initialized();
  EnterCriticalSection(&_cs_use_cache);
  _use_cache = use;
  LeaveCriticalSection(&_cs_use_cache);
}

PORT
void destroy_impulse_cache(void) {
  ensure_impulse_cache_initialized();
  EnterCriticalSection(&_cs_use_cache);
  _use_cache = 0;
  LeaveCriticalSection(&_cs_use_cache);
  free_impulse_cache();
}
