import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';
import { downloadBlob } from './download';
import { mockActivity } from './mock-activity';
import {
  ClusterResponse,
  EngineInfo,
  HistogramResponse,
  TypeMetric,
  TypeScheduleUpdate,
  TypeSettingsResponse,
  Wave,
  WaveActivityResponse,
  WaveFilter,
  WavesResponse
} from './models';

/** Calls the Ripple read API (dev proxies /api to the sample WebAPI; same origin in prod). */
@Injectable({ providedIn: 'root' })
export class RippleApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  getWaves(filter: WaveFilter = {}, limit = 50): Observable<WavesResponse> {
    return this.http.get<WavesResponse>(`${this.base}/waves`, { params: this.toParams(filter, limit) });
  }

  /** Per-day wave counts for the contribution heatmap. Falls back to generated data (no backend in dev). */
  getWaveActivity(days = 365): Observable<WaveActivityResponse> {
    return this.http
      .get<WaveActivityResponse>(`${this.base}/waves/activity`, { params: new HttpParams().set('days', days) })
      .pipe(catchError(() => of(mockActivity(days))));
  }

  /** Wave counts bucketed by a time granularity over [from, to) — for the adaptive tile-zoom. */
  getWavesHistogram(from: string, to: string, bucket: 'year' | 'day' | 'hour' | 'minute' | 'second'): Observable<HistogramResponse> {
    // Send the browser timezone so the server buckets on the SAME day/hour boundaries the calendar renders and
    // drills in on (see /waves/histogram) — otherwise a green cell can zoom into zero waves across a tz gap.
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const params = new HttpParams().set('from', from).set('to', to).set('bucket', bucket).set('tz', tz);
    return this.http.get<HistogramResponse>(`${this.base}/waves/histogram`, { params });
  }

  getWave(waveId: string): Observable<Wave> {
    return this.http.get<Wave>(`${this.base}/waves/${waveId}`);
  }

  getTypeMetrics(): Observable<TypeMetric[]> {
    return this.http.get<TypeMetric[]>(`${this.base}/metrics/types`);
  }

  getCluster(): Observable<ClusterResponse> {
    return this.http.get<ClusterResponse>(`${this.base}/cluster`);
  }

  /** The scheduler's per-type config (the DEFAULT row + each registered type), for the Settings page. */
  getSettings(): Observable<TypeSettingsResponse> {
    return this.http.get<TypeSettingsResponse>(`${this.base}/settings/types`);
  }

  /** Read-only engine options for one worker instance. */
  getEngineInfo(): Observable<EngineInfo> {
    return this.http.get<EngineInfo>(`${this.base}/settings/engine`);
  }

  /** Create/overwrite a type's config (typeKey '__default__' edits the default row). */
  updateTypeSetting(typeKey: string, body: TypeScheduleUpdate): Observable<void> {
    return this.http.put<void>(`${this.base}/settings/types/${encodeURIComponent(typeKey)}`, body);
  }

  /** Reset a type to the default by deleting its row. */
  resetTypeSetting(typeKey: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/settings/types/${encodeURIComponent(typeKey)}`);
  }

  /** Pause a type: park its pending ripples so the claim skips them until resumed. */
  pauseType(typeKey: string): Observable<void> {
    return this.http.post<void>(`${this.base}/settings/types/${encodeURIComponent(typeKey)}/pause`, null);
  }

  /**
   * Resume a paused type. `rebase` true (default) re-stamps the parked work onto the current frontier so it
   * interleaves fairly; false resumes it "as-is" (it runs ahead of everything to catch up).
   */
  resumeType(typeKey: string, rebase: boolean): Observable<void> {
    const params = new HttpParams().set('rebase', rebase);
    return this.http.post<void>(`${this.base}/settings/types/${encodeURIComponent(typeKey)}/resume`, null, { params });
  }

  /**
   * Fetches the aggregated splash report and triggers a browser CSV download.
   * Emits `true` when the file downloaded, `false` when the report is still pending (wave not yet compacted).
   */
  downloadReportCsv(waveId: string): Observable<boolean> {
    return this.http
      .get(`${this.base}/waves/${waveId}/report.csv`, { observe: 'response', responseType: 'blob' })
      .pipe(
        map((resp) => {
          if (resp.status === 200 && resp.body) {
            downloadBlob(resp.body, `wave-${waveId}-report.csv`);
            return true;
          }
          return false; // 202 Accepted ⇒ report pending until the wave compacts
        })
      );
  }

  private toParams(filter: object, limit: number): HttpParams {
    let params = new HttpParams().set('limit', limit);
    for (const [key, value] of Object.entries(filter)) {
      if (typeof value === 'string' && value.length > 0) {
        params = params.set(key, value);
      }
    }
    return params;
  }
}
