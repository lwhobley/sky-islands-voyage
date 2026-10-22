import { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import { AppModule } from '../app.module';
import { RpcError, SupabaseService } from '../supabase.service';

const KEY = 'k'.repeat(40);

describe('Admin catalog API', () => {
  let app: INestApplication;
  let base: string;
  const rpc = jest.fn();

  beforeAll(async () => {
    process.env.SUPABASE_URL = 'https://example.supabase.co';
    process.env.SUPABASE_SERVICE_ROLE_KEY = 'test-key';
    process.env.ADMIN_API_KEY = KEY;
    const moduleRef = await Test.createTestingModule({ imports: [AppModule] })
      .overrideProvider(SupabaseService)
      .useValue({ rpc, verifyPlayerToken: async () => null })
      .compile();
    app = moduleRef.createNestApplication({ logger: false });
    await app.listen(0);
    base = await app.getUrl();
  });
  afterAll(() => app.close());
  beforeEach(() => rpc.mockReset());

  const headers = { 'content-type': 'application/json', 'x-admin-key': KEY, 'x-admin-actor': 'ops@studio' };
  const call = (path: string, method = 'GET', body?: unknown, h: Record<string, string> = headers) =>
    fetch(`${base}${path}`, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) });

  const season = {
    name: 'Season 2',
    startsAt: '2026-12-01T00:00:00Z',
    endsAt: '2026-12-31T00:00:00Z',
    sets: [{ name: 'Harbor', rewardRolls: 40, rewardCrystalRolls: 60, cards: [{ name: 'Sprite', rarity: 1, dropWeight: 100 }] }],
  };

  it('requires the admin key', async () => {
    expect((await call('/admin/seasons', 'GET', undefined, {})).status).toBe(401);
    expect((await call('/admin/products', 'GET', undefined, { 'x-admin-key': 'nope', 'x-admin-actor': 'x' })).status).toBe(401);
    expect(rpc).not.toHaveBeenCalled();
  });

  it('lists seasons and products', async () => {
    rpc.mockResolvedValue([]);
    expect((await call('/admin/seasons')).status).toBe(200);
    expect(rpc).toHaveBeenLastCalledWith('admin_list_seasons');
    expect((await call('/admin/products')).status).toBe(200);
    expect(rpc).toHaveBeenLastCalledWith('admin_list_products');
  });

  it('creates a season, translating camelCase to the database shape', async () => {
    rpc.mockResolvedValue({ season_id: 2, sets: 1, cards: 1 });
    const res = await call('/admin/seasons', 'POST', season);
    expect(res.status).toBe(201);
    expect(await res.json()).toEqual({ season_id: 2, sets: 1, cards: 1 });
    expect(rpc).toHaveBeenCalledWith('admin_create_season', {
      p_actor: 'ops@studio',
      p_season: {
        name: 'Season 2',
        starts_at: '2026-12-01T00:00:00Z',
        ends_at: '2026-12-31T00:00:00Z',
        sets: [{ name: 'Harbor', reward_rolls: 40, reward_crystal_rolls: 60, cards: [{ name: 'Sprite', rarity: 1, drop_weight: 100 }] }],
      },
    });
  });

  it('rejects malformed season bodies before touching the database', async () => {
    expect((await call('/admin/seasons', 'POST', { ...season, sets: 'nope' })).status).toBe(400);
    expect((await call('/admin/seasons', 'POST', { ...season, sets: [{ name: 'x' }] })).status).toBe(400);
    expect((await call('/admin/seasons', 'POST', { ...season, sets: [{ name: 'x', cards: [7] }] })).status).toBe(400);
    expect((await call('/admin/seasons', 'POST', [])).status).toBe(400);
    expect(rpc).not.toHaveBeenCalled();
  });

  it('maps database refusals', async () => {
    rpc.mockRejectedValueOnce(new RpcError('invalid_season', '22023'));
    expect((await call('/admin/seasons', 'POST', season)).status).toBe(422);
    rpc.mockRejectedValueOnce(new RpcError('season_overlaps', 'P0001'));
    expect((await call('/admin/seasons', 'POST', season)).status).toBe(409);
  });

  it('deletes upcoming seasons only', async () => {
    rpc.mockResolvedValue(null);
    expect((await call('/admin/seasons/5', 'DELETE')).status).toBe(204);
    expect(rpc).toHaveBeenCalledWith('admin_delete_season', { p_actor: 'ops@studio', p_season_id: 5 });
    expect((await call('/admin/seasons/abc', 'DELETE')).status).toBe(400);
    expect((await call('/admin/seasons/99999', 'DELETE')).status).toBe(400);
    rpc.mockRejectedValueOnce(new RpcError('season_not_found', 'P0001'));
    expect((await call('/admin/seasons/6', 'DELETE')).status).toBe(404);
    rpc.mockRejectedValueOnce(new RpcError('season_already_started', 'P0001'));
    expect((await call('/admin/seasons/7', 'DELETE')).status).toBe(409);
  });

  it('upserts products', async () => {
    rpc.mockResolvedValue({ product_id: 'sky.rolls.small', rolls: 150 });
    const res = await call('/admin/products/sky.rolls.small', 'PUT', { rolls: 150, cardPacks: 0, active: true });
    expect(res.status).toBe(200);
    expect(rpc).toHaveBeenCalledWith('admin_set_product', {
      p_actor: 'ops@studio', p_product_id: 'sky.rolls.small', p_rolls: 150, p_card_packs: 0, p_active: true,
    });
    expect((await call('/admin/products/sky.rolls.small', 'PUT', { rolls: 150 })).status).toBe(400);
    rpc.mockRejectedValueOnce(new RpcError('invalid_product', '22023'));
    expect((await call('/admin/products/x', 'PUT', { rolls: 1, active: true })).status).toBe(422);
  });
});
