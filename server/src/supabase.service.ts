import { Inject, Injectable } from '@nestjs/common';
import { createClient, SupabaseClient } from '@supabase/supabase-js';
import { APP_CONFIG, AppConfig } from './config';

export class RpcError extends Error {
  constructor(message: string, readonly code?: string) {
    super(message);
  }
}

/** Service-role access to the game database. Bypasses RLS — only call the service RPCs. */
@Injectable()
export class SupabaseService {
  private readonly client: SupabaseClient;

  constructor(@Inject(APP_CONFIG) config: AppConfig) {
    this.client = createClient(config.supabaseUrl, config.supabaseServiceKey, {
      auth: { persistSession: false, autoRefreshToken: false },
    });
  }

  async rpc<T>(fn: string, args: Record<string, unknown> = {}): Promise<T> {
    const { data, error } = await this.client.rpc(fn, args);
    if (error) throw new RpcError(error.message, error.code);
    return data as T;
  }

  /** Verifies a player's access token (JWKS or Auth server) and returns its user id. */
  async verifyPlayerToken(jwt: string): Promise<string | null> {
    const { data, error } = await this.client.auth.getClaims(jwt);
    if (error || !data) return null;
    const { sub, role } = data.claims;
    return role === 'authenticated' && typeof sub === 'string' ? sub : null;
  }
}
