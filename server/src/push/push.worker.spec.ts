import { AppConfig } from '../config';
import { SupabaseService } from '../supabase.service';
import { PushResult, PushSender } from './push.sender';
import { OutboxItem, PushWorker, toPushMessage } from './push.worker';

const config = { pushPollMs: 1000 } as AppConfig;

function item(id: number, tokens: string[], payload: Record<string, unknown> = {}): OutboxItem {
  return {
    id,
    user_id: `user-${id}`,
    payload: { title: 'Your island was raided!', body: 'Ada took 500 crystals.', priority: 'high', ...payload },
    tokens: tokens.map((token) => ({ token, platform: 'android' as const })),
  };
}

function setup(batch: OutboxItem[], send: (tokens: string[]) => Promise<PushResult>) {
  const rpc = jest.fn(async (fn: string, _args?: Record<string, unknown>) => (fn === 'claim_push_batch' ? batch : null));
  const sender: PushSender = { send: jest.fn((tokens) => send(tokens)) };
  const worker = new PushWorker({ rpc } as unknown as SupabaseService, sender, config);
  return { worker, rpc, sender };
}

const ok = (n: number): PushResult => ({ successCount: n, invalidTokens: [], transientFailure: false });

describe('PushWorker.tick', () => {
  it('delivers, acknowledges, and prunes dead tokens', async () => {
    const { worker, rpc } = setup([item(1, ['a', 'dead']), item(2, ['b'])], async (tokens) =>
      tokens.includes('dead') ? { successCount: 1, invalidTokens: ['dead'], transientFailure: false } : ok(1),
    );

    expect(await worker.tick()).toBe(2);

    expect(rpc).toHaveBeenCalledWith('claim_push_batch', { p_limit: 100 });
    const [, args] = rpc.mock.calls.find(([fn]) => fn === 'complete_push_batch')!;
    expect((args as { p_ids: number[] }).p_ids.sort()).toEqual([1, 2]);
    expect(args).toMatchObject({ p_invalid_tokens: ['dead'] });
  });

  it('acknowledges players without devices without sending', async () => {
    const { worker, rpc, sender } = setup([item(7, [])], async () => ok(0));
    await worker.tick();
    expect(sender.send).not.toHaveBeenCalled();
    expect(rpc).toHaveBeenCalledWith('complete_push_batch', { p_ids: [7], p_invalid_tokens: [] });
  });

  it('leaves fully failed or thrown sends for retry', async () => {
    const { worker, rpc } = setup([item(1, ['a']), item(2, ['b'])], async (tokens) => {
      if (tokens[0] === 'a') return { successCount: 0, invalidTokens: [], transientFailure: true };
      throw new Error('FCM unavailable');
    });
    await worker.tick();
    expect(rpc.mock.calls.some(([fn]) => fn === 'complete_push_batch')).toBe(false);
  });

  it('does nothing when the outbox is empty', async () => {
    const { worker, rpc } = setup([], async () => ok(0));
    expect(await worker.tick()).toBe(0);
    expect(rpc).toHaveBeenCalledTimes(1);
  });
});

describe('toPushMessage', () => {
  it('stringifies data, drops nulls, and maps priority', () => {
    const msg = toPushMessage({
      type: 'revenge', priority: 'high', raid_id: 'r1', crystals_lost: '15000', damaged_slot: 1, shielded: false,
      attacker_name: 'Ada', title: 'Raided!', body: 'Strike back', attacker_id: null,
    });
    expect(msg).toEqual({
      title: 'Raided!',
      body: 'Strike back',
      priority: 'high',
      data: { type: 'revenge', raid_id: 'r1', attacker_name: 'Ada', crystals_lost: '15000', damaged_slot: '1', shielded: 'false' },
    });
    expect(toPushMessage({ priority: 'normal' }).priority).toBe('normal');
  });
});
