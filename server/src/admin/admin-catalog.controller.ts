import {
  BadRequestException,
  Body,
  Controller,
  Delete,
  Get,
  HttpCode,
  HttpStatus,
  NotFoundException,
  Param,
  Post,
  Put,
  UnprocessableEntityException,
  ConflictException,
  UseGuards,
} from '@nestjs/common';
import { RpcError, SupabaseService } from '../supabase.service';
import { AdminActor, AdminGuard } from './admin.guard';

/** Live-ops catalog: card seasons and store products. Everything is audited in admin_actions. */
@Controller('admin')
@UseGuards(AdminGuard)
export class AdminCatalogController {
  constructor(private readonly supabase: SupabaseService) {}

  /** GET /admin/seasons — newest first, with upcoming/live/ended state and set/card counts. */
  @Get('seasons')
  seasons() {
    return this.supabase.rpc('admin_list_seasons');
  }

  /**
   * POST /admin/seasons — creates a whole season atomically:
   *   { name, startsAt, endsAt, sets: [{ name, rewardRolls, rewardCrystalRolls, cards: [{ name, rarity 1-5, dropWeight 1-1000 }] }] }
   * Seasons cannot overlap and cannot be edited once created (players' albums reference their cards).
   */
  @Post('seasons')
  async createSeason(@AdminActor() actor: string, @Body() body: unknown) {
    const b = asObject(body, 'body');
    const sets = b.sets;
    if (!Array.isArray(sets)) throw new BadRequestException('sets must be an array');
    const season = {
      name: b.name,
      starts_at: b.startsAt,
      ends_at: b.endsAt,
      sets: sets.map((s, i) => {
        const set = asObject(s, `sets[${i}]`);
        if (!Array.isArray(set.cards)) throw new BadRequestException(`sets[${i}].cards must be an array`);
        return {
          name: set.name,
          reward_rolls: set.rewardRolls ?? 0,
          reward_crystal_rolls: set.rewardCrystalRolls ?? 0,
          cards: set.cards.map((c, j) => {
            const card = asObject(c, `sets[${i}].cards[${j}]`);
            return { name: card.name, rarity: card.rarity, drop_weight: card.dropWeight };
          }),
        };
      }),
    };
    try {
      return await this.supabase.rpc('admin_create_season', { p_actor: actor, p_season: season });
    } catch (e) {
      if (e instanceof RpcError && e.message === 'invalid_season') {
        throw new UnprocessableEntityException('invalid_season: check names, dates, rarity 1-5, dropWeight 1-1000 and set/card counts');
      }
      if (e instanceof RpcError && e.message === 'season_overlaps') throw new ConflictException(e.message);
      throw e;
    }
  }

  /** DELETE /admin/seasons/:id — only before the season starts. */
  @Delete('seasons/:id')
  @HttpCode(HttpStatus.NO_CONTENT)
  async deleteSeason(@AdminActor() actor: string, @Param('id') id: string) {
    if (!/^\d{1,5}$/.test(id) || Number(id) > 32767) throw new BadRequestException('id must be a season id');
    try {
      await this.supabase.rpc('admin_delete_season', { p_actor: actor, p_season_id: Number(id) });
    } catch (e) {
      if (e instanceof RpcError && e.message === 'season_not_found') throw new NotFoundException(e.message);
      if (e instanceof RpcError && e.message === 'season_already_started') throw new ConflictException(e.message);
      throw e;
    }
  }

  /** GET /admin/products */
  @Get('products')
  products() {
    return this.supabase.rpc('admin_list_products');
  }

  /**
   * PUT /admin/products/:productId  { rolls, cardPacks, active }
   * productId must equal the id configured in App Store Connect / Play Console.
   * Deactivate instead of deleting; purchases reference the product.
   */
  @Put('products/:productId')
  async setProduct(@AdminActor() actor: string, @Param('productId') productId: string, @Body() body: unknown) {
    const b = asObject(body, 'body');
    if (typeof b.active !== 'boolean') throw new BadRequestException('active must be a boolean');
    try {
      return await this.supabase.rpc('admin_set_product', {
        p_actor: actor,
        p_product_id: productId,
        p_rolls: b.rolls ?? 0,
        p_card_packs: b.cardPacks ?? 0,
        p_active: b.active,
      });
    } catch (e) {
      if (e instanceof RpcError && e.message === 'invalid_product') {
        throw new UnprocessableEntityException('invalid_product: id is 3-100 of A-Z a-z 0-9 . _ -, rolls 0-1000000, cardPacks 0-10000');
      }
      throw e;
    }
  }
}

function asObject(value: unknown, name: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new BadRequestException(`${name} must be an object`);
  return value as Record<string, unknown>;
}
