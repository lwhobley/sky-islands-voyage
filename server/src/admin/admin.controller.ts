import { BadRequestException, Body, Controller, Get, HttpCode, HttpStatus, NotFoundException, Post, Query, UseGuards } from '@nestjs/common';
import { RpcError, SupabaseService } from '../supabase.service';
import { AdminActor, AdminGuard } from './admin.guard';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminController {
  constructor(private readonly supabase: SupabaseService) {}

  /**
   * GET /admin/anomalies?minRolls=200&minZ=4
   * Players whose win rate at >=10x multipliers is statistically above their rate at <=2x —
   * the signature of a modified client that peeks at upcoming rolls. Review before acting.
   */
  @Get('anomalies')
  anomalies(@Query('minRolls') minRolls?: string, @Query('minZ') minZ?: string) {
    return this.supabase.rpc('admin_multiplier_anomalies', {
      p_min_rolls: intParam(minRolls, 200, 50, 1_000_000, 'minRolls'),
      p_min_z: numberParam(minZ, 4, 'minZ'),
    });
  }

  /**
   * POST /admin/grants  { userId, rolls?, cardPacks?, crystals?, reason }
   * Compensation / goodwill grant, recorded in public.admin_actions with the operator.
   * Returns the player's new state.
   */
  @Post('grants')
  @HttpCode(HttpStatus.OK)
  async grant(@AdminActor() actor: string, @Body() body: unknown) {
    const b = (body ?? {}) as Record<string, unknown>;
    if (typeof b.userId !== 'string' || !UUID.test(b.userId)) throw new BadRequestException('userId must be a UUID');
    if (typeof b.reason !== 'string' || b.reason.trim().length === 0) throw new BadRequestException('reason is required');
    const crystals = b.crystals ?? '0';
    if (typeof crystals !== 'string' || !/^\d{1,40}$/.test(crystals)) {
      throw new BadRequestException('crystals must be a non-negative integer string');
    }
    try {
      return await this.supabase.rpc('admin_grant', {
        p_actor: actor,
        p_user_id: b.userId,
        p_rolls: wholeNumber(b.rolls, 'rolls'),
        p_card_packs: wholeNumber(b.cardPacks, 'cardPacks'),
        p_crystals: crystals,
        p_reason: b.reason.trim().slice(0, 500),
      });
    } catch (e) {
      if (e instanceof RpcError && e.message === 'player_not_found') throw new NotFoundException(e.message);
      if (e instanceof RpcError && (e.message === 'grant_out_of_bounds' || e.message === 'actor_and_reason_required')) {
        throw new BadRequestException(e.message);
      }
      throw e;
    }
  }
}

function wholeNumber(value: unknown, name: string): number {
  if (value === undefined) return 0;
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0) {
    throw new BadRequestException(`${name} must be a non-negative integer`);
  }
  return value;
}

function intParam(value: string | undefined, fallback: number, min: number, max: number, name: string): number {
  if (value === undefined) return fallback;
  const n = Number(value);
  if (!Number.isInteger(n) || n < min || n > max) throw new BadRequestException(`${name} must be an integer in ${min}..${max}`);
  return n;
}

function numberParam(value: string | undefined, fallback: number, name: string): number {
  if (value === undefined) return fallback;
  const n = Number(value);
  if (!Number.isFinite(n)) throw new BadRequestException(`${name} must be a number`);
  return n;
}
