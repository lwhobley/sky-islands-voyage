import { Inject, Injectable, Logger, OnApplicationBootstrap, OnApplicationShutdown } from '@nestjs/common';
import { APP_CONFIG, AppConfig } from '../config';
import { SupabaseService } from '../supabase.service';
import { PUSH_SENDER, PushMessage, PushSender } from './push.sender';

const BATCH_SIZE = 100;

/** One row returned by public.claim_push_batch. */
export interface OutboxItem {
  id: number;
  user_id: string;
  payload: Record<string, unknown>;
  tokens: { token: string; platform: 'ios' | 'android' }[];
}

/**
 * Drains public.notification_outbox (filled by a trigger on raid_history) into FCM.
 * Claims are leased for 2 minutes in SQL, so several instances can run side by side and a
 * crashed instance's batch is retried. Delivery is at-least-once.
 */
@Injectable()
export class PushWorker implements OnApplicationBootstrap, OnApplicationShutdown {
  private readonly log = new Logger(PushWorker.name);
  private timer?: NodeJS.Timeout;
  private stopped = false;

  constructor(
    private readonly supabase: SupabaseService,
    @Inject(PUSH_SENDER) private readonly sender: PushSender | null,
    @Inject(APP_CONFIG) private readonly config: AppConfig,
  ) {}

  onApplicationBootstrap() {
    if (!this.sender) {
      this.log.warn('Push delivery disabled: FIREBASE_SERVICE_ACCOUNT_KEY_FILE is not set');
      return;
    }
    this.schedule(0);
  }

  onApplicationShutdown() {
    this.stopped = true;
    clearTimeout(this.timer);
  }

  /** Claims and delivers one batch; returns how many notifications were claimed. */
  async tick(): Promise<number> {
    const batch = await this.supabase.rpc<OutboxItem[]>('claim_push_batch', { p_limit: BATCH_SIZE });
    const delivered: number[] = [];
    const invalidTokens: string[] = [];

    await Promise.all(
      batch.map(async (item) => {
        if (item.tokens.length === 0) {
          delivered.push(item.id); // nobody to notify; Realtime covers players in-game
          return;
        }
        try {
          const result = await this.sender!.send(item.tokens.map((t) => t.token), toPushMessage(item.payload));
          invalidTokens.push(...result.invalidTokens);
          // Retry only if nothing got through; partial delivery is good enough for a nudge.
          if (result.successCount > 0 || !result.transientFailure) delivered.push(item.id);
        } catch (e) {
          this.log.warn(`Push ${item.id} failed, will retry: ${(e as Error).message}`);
        }
      }),
    );

    if (delivered.length > 0 || invalidTokens.length > 0) {
      await this.supabase.rpc('complete_push_batch', { p_ids: delivered, p_invalid_tokens: invalidTokens });
    }
    return batch.length;
  }

  private schedule(delayMs: number) {
    if (this.stopped) return;
    this.timer = setTimeout(() => void this.loop(), delayMs);
  }

  private async loop() {
    let claimed = 0;
    try {
      claimed = await this.tick();
    } catch (e) {
      this.log.error(`Push batch failed: ${(e as Error).message}`);
    }
    // A full batch means more is waiting: go again immediately.
    this.schedule(claimed >= BATCH_SIZE ? 0 : this.config.pushPollMs);
  }
}

/** Maps a raid_history.notification payload to an FCM message (data values must be strings). */
export function toPushMessage(payload: Record<string, unknown>): PushMessage {
  const data: Record<string, string> = {};
  for (const key of ['type', 'raid_id', 'attacker_id', 'attacker_name', 'crystals_lost', 'damaged_slot', 'shielded']) {
    const value = payload[key];
    if (value !== null && value !== undefined) data[key] = String(value);
  }
  return {
    title: String(payload.title ?? 'Sky Islands'),
    body: String(payload.body ?? ''),
    priority: payload.priority === 'high' ? 'high' : 'normal',
    data,
  };
}
