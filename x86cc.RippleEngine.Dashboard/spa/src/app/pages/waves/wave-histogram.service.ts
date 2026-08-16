import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { shareReplay } from 'rxjs/operators';
import { RippleApiService } from '../../core/ripple-api.service';
import { HistogramResponse } from '../../core/models';

type Bucket = 'year' | 'day' | 'hour' | 'minute' | 'second';

// On one navigation the shell's pills and the active zoom page often need the SAME histogram (same range +
// granularity). This shares a single in-flight/just-fetched request per (from,to,bucket) for a short window so
// it hits the API once instead of twice. The TTL is small so data stays fresh across real navigations.
const CACHE_TTL_MS = 2000;

@Injectable({ providedIn: 'root' })
export class WaveHistogramService {
  private readonly api = inject(RippleApiService);
  private readonly inflight = new Map<string, Observable<HistogramResponse>>();

  histogram(from: string, to: string, bucket: Bucket): Observable<HistogramResponse> {
    const key = `${from}|${to}|${bucket}`;
    let shared = this.inflight.get(key);
    if (!shared) {
      shared = this.api.getWavesHistogram(from, to, bucket).pipe(shareReplay({ bufferSize: 1, refCount: false }));
      this.inflight.set(key, shared);
      setTimeout(() => this.inflight.delete(key), CACHE_TTL_MS);
    }
    return shared;
  }
}
