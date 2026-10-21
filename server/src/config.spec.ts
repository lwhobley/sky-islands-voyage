import { loadConfig } from './config';

const base = { SUPABASE_URL: 'https://x.supabase.co', SUPABASE_SERVICE_ROLE_KEY: 'secret' };

describe('loadConfig', () => {
  it('requires Supabase', () => {
    expect(() => loadConfig({})).toThrow('SUPABASE_URL');
  });

  it('leaves unconfigured integrations disabled', () => {
    const config = loadConfig(base);
    expect(config.apple).toBeUndefined();
    expect(config.google).toBeUndefined();
    expect(config.firebase).toBeUndefined();
    expect(config.port).toBe(3000);
  });

  it('rejects a partially configured integration', () => {
    expect(() => loadConfig({ ...base, GOOGLE_PACKAGE_NAME: 'com.example' })).toThrow('GOOGLE_SERVICE_ACCOUNT_KEY_FILE');
  });

  it('requires a long admin key when one is set', () => {
    expect(() => loadConfig({ ...base, ADMIN_API_KEY: 'short' })).toThrow('ADMIN_API_KEY');
    expect(loadConfig({ ...base, ADMIN_API_KEY: 'x'.repeat(32) }).adminApiKey).toHaveLength(32);
  });

  it('enables Google when complete', () => {
    const config = loadConfig({ ...base, GOOGLE_PACKAGE_NAME: 'com.example', GOOGLE_SERVICE_ACCOUNT_KEY_FILE: 'k.json' });
    expect(config.google).toEqual({ packageName: 'com.example', serviceAccountKeyFile: 'k.json' });
  });
});
