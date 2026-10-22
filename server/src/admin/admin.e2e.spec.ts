import { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import { AppModule } from '../app.module';
import { RpcError, SupabaseService } from '../supabase.service';

const KEY = 'k'.repeat(40);
const PLAYER = '33333333-3333-3333-3333-333333333333';

async function boot(adminKey?: string) {
  process.env.SUPABASE_URL = 'https://example.supabase.co';
  process.env.SUPABASE_SERVICE_ROLE_KEY = 'test-key';
  if (adminKey) process.env.ADMIN_API_KEY = adminKey;
  else delete process.env.ADMIN_API_KEY;
  const rpc = jest.fn();
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] })
    .overrideProvider(SupabaseService)
    .useValue({ rpc, verifyPlayerToken: async () => null })
    .compile();
  const app = moduleRef.createNestApplication({ logger: false });
  await app.listen(0);
  return { app, rpc, base: await app.getUrl() };
}

describe('Admin API', () => {
  let app: INestApplication;
  let rpc: jest.Mock;
  let base: string;

  beforeAll(async () => ({ app, rpc, base } = await boot(KEY)));
  afterAll(() => app.close());
  beforeEach(() => rpc.mockReset());

  const call = (path: string, init: RequestInit = {}, headers: Record<string, string> = { 'x-admin-key': KEY, 'x-admin-actor': 'ops@studio' }) =>
    fetch(`${base}${path}`, { ...init, headers: { 'content-type': 'application/json', ...headers } });

  it('rejects missing or wrong keys and anonymous operators', async () => {
    expect((await call('/admin/anomalies', {}, {})).status).toBe(401);
    expect((await call('/admin/anomalies', {}, { 'x-admin-key': 'nope', 'x-admin-actor': 'ops' })).status).toBe(401);
    expect((await call('/admin/anomalies', {}, { 'x-admin-key': KEY })).status).toBe(401);
    expect(rpc).not.toHaveBeenCalled();
  });

  it('reports anomalies with validated thresholds', async () => {
    rpc.mockResolvedValue([{ user_id: PLAYER, z: 38.7 }]);
    const res = await call('/admin/anomalies?minRolls=500&minZ=5');
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual([{ user_id: PLAYER, z: 38.7 }]);
    expect(rpc).toHaveBeenCalledWith('admin_multiplier_anomalies', { p_min_rolls: 500, p_min_z: 5 });
    expect((await call('/admin/anomalies?minRolls=3')).status).toBe(400);
  });

  it('grants compensation on behalf of the named operator', async () => {
    rpc.mockResolvedValue({ rolls: 300 });
    const res = await call('/admin/grants', {
      method: 'POST',
      body: JSON.stringify({ userId: PLAYER, rolls: 250, crystals: '1000', reason: ' Outage ' }),
    });
    expect(res.status).toBe(200);
    expect(rpc).toHaveBeenCalledWith('admin_grant', {
      p_actor: 'ops@studio', p_user_id: PLAYER, p_rolls: 250, p_card_packs: 0, p_crystals: '1000', p_reason: 'Outage',
    });
  });

  it('validates grant bodies and maps database refusals', async () => {
    const post = (body: unknown) => call('/admin/grants', { method: 'POST', body: JSON.stringify(body) });
    expect((await post({ userId: 'x', reason: 'r' })).status).toBe(400);
    expect((await post({ userId: PLAYER })).status).toBe(400);
    expect((await post({ userId: PLAYER, reason: 'r', rolls: -1 })).status).toBe(400);
    expect((await post({ userId: PLAYER, reason: 'r', crystals: 1.5 })).status).toBe(400);
    expect(rpc).not.toHaveBeenCalled();

    rpc.mockRejectedValueOnce(new RpcError('player_not_found', 'P0001'));
    expect((await post({ userId: PLAYER, reason: 'r', rolls: 1 })).status).toBe(404);
    rpc.mockRejectedValueOnce(new RpcError('grant_out_of_bounds', '22023'));
    expect((await post({ userId: PLAYER, reason: 'r', rolls: 1 })).status).toBe(400);
  });
});

describe('Admin report queue', () => {
  let app: INestApplication;
  let rpc: jest.Mock;
  let base: string;

  beforeAll(async () => ({ app, rpc, base } = await boot(KEY)));
  afterAll(() => app.close());
  beforeEach(() => rpc.mockReset());

  const headers = { 'content-type': 'application/json', 'x-admin-key': KEY, 'x-admin-actor': 'mod@studio' };
  const resolve = (id: string, body: unknown) =>
    fetch(`${base}/admin/reports/${id}/resolve`, { method: 'POST', headers, body: JSON.stringify(body) });

  it('lists reports by status', async () => {
    rpc.mockResolvedValue([]);
    expect((await fetch(`${base}/admin/reports`, { headers })).status).toBe(200);
    expect(rpc).toHaveBeenCalledWith('admin_list_reports', { p_status: 'open', p_limit: 50 });
    expect((await fetch(`${base}/admin/reports?status=deleted`, { headers })).status).toBe(400);
  });

  it('resolves with optional actions on behalf of the moderator', async () => {
    rpc.mockResolvedValue({ id: 7, status: 'actioned' });
    const res = await resolve('7', { status: 'actioned', note: 'Chat abuse', resetName: true, deleteMessage: true });
    expect(res.status).toBe(200);
    expect(rpc).toHaveBeenCalledWith('admin_resolve_report', {
      p_actor: 'mod@studio', p_report_id: 7, p_status: 'actioned', p_note: 'Chat abuse',
      p_reset_name: true, p_delete_message: true,
    });
  });

  it('validates input and maps conflicts', async () => {
    expect((await resolve('abc', { status: 'actioned', note: 'n' })).status).toBe(400);
    expect((await resolve('7', { status: 'open', note: 'n' })).status).toBe(400);
    expect((await resolve('7', { status: 'dismissed' })).status).toBe(400);
    expect((await resolve('7', { status: 'dismissed', note: 'n', resetName: 'yes' })).status).toBe(400);
    expect(rpc).not.toHaveBeenCalled();
    rpc.mockRejectedValueOnce(new RpcError('report_not_found', 'P0001'));
    expect((await resolve('8', { status: 'dismissed', note: 'n' })).status).toBe(404);
    rpc.mockRejectedValueOnce(new RpcError('report_already_resolved', 'P0001'));
    expect((await resolve('8', { status: 'dismissed', note: 'n' })).status).toBe(409);
  });
});

describe('Admin API without ADMIN_API_KEY', () => {
  it('is disabled', async () => {
    const { app, base } = await boot();
    const res = await fetch(`${base}/admin/anomalies`, { headers: { 'x-admin-key': KEY, 'x-admin-actor': 'ops' } });
    expect(res.status).toBe(403);
    await app.close();
  });
});
