import { CanActivate, createParamDecorator, ExecutionContext, Injectable, UnauthorizedException } from '@nestjs/common';
import type { Request } from 'express';
import { SupabaseService } from './supabase.service';

type PlayerRequest = Request & { playerId?: string };

/** Requires `Authorization: Bearer <Supabase access token>` from the game client. */
@Injectable()
export class PlayerGuard implements CanActivate {
  constructor(private readonly supabase: SupabaseService) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const req = context.switchToHttp().getRequest<PlayerRequest>();
    const match = /^Bearer (.+)$/.exec(req.headers.authorization ?? '');
    if (!match) throw new UnauthorizedException('missing_token');
    const playerId = await this.supabase.verifyPlayerToken(match[1]);
    if (!playerId) throw new UnauthorizedException('invalid_token');
    req.playerId = playerId;
    return true;
  }
}

export const PlayerId = createParamDecorator(
  (_: unknown, context: ExecutionContext): string => context.switchToHttp().getRequest<PlayerRequest>().playerId!,
);
