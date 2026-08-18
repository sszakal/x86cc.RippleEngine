import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The brand mark: a drop with rings spreading out of it, rippling continuously.
 *
 * The motion is decoration, not a status indicator — it says nothing about what the cluster is doing, so the
 * loop just runs. The static concentric-target glyph underneath is the `prefers-reduced-motion` fallback, so
 * it still has to read as a finished mark on its own.
 *
 * Presentational by design: it takes nothing and draws. Hidden from assistive tech — the wordmark beside it
 * already carries the name, and an endless animation has no state worth announcing.
 */
@Component({
  selector: 'app-ripple-logo',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    'aria-hidden': 'true'
  },
  styles: [
    `
      :host {
        position: relative;
        display: block;
        /* 36px — the footprint of the lettermark tile this replaces, so the sidebar header doesn't reflow. */
        width: 2.25rem;
        height: 2.25rem;
        flex: none;
      }

      /*
       * Every ring runs the same expand-and-fade, offset by a third of the cycle each — the CSS equivalent of
       * the motion.dev example's stagger(). Only transform and opacity animate, so this stays on the
       * compositor and costs nothing while it loops.
       */
      .ring {
        position: absolute;
        inset: 0;
        transform-origin: center;
        will-change: transform, opacity;
        animation: ripple-out 2.4s cubic-bezier(0, 0, 0.2, 1) infinite;
      }

      /*
       * The transform/opacity here are what shows with the animation off (the reduced-motion fallback, and
       * the delayed rings' first frames): a static target, each ring a step further out and a step fainter,
       * so the mark still reads as "spreading" with nothing moving.
       */
      .ring:nth-child(1) { transform: scale(0.45); opacity: 0.5; }
      .ring:nth-child(2) { transform: scale(0.72); opacity: 0.28; animation-delay: 0.8s; }
      .ring:nth-child(3) { transform: scale(1);    opacity: 0.14; animation-delay: 1.6s; }

      @keyframes ripple-out {
        0%   { transform: scale(0.28); opacity: 0.85; }
        70%  { opacity: 0.22; }
        100% { transform: scale(1);    opacity: 0; }
      }

      /* Drop back to the static glyph: with the animation off, the base .ring rules apply again. */
      @media (prefers-reduced-motion: reduce) {
        .ring { animation: none; }
      }

      .drop {
        position: absolute;
        top: 50%;
        left: 50%;
        width: 10px;
        height: 10px;
        margin: -5px 0 0 -5px;
      }
    `
  ],
  // Colour comes from Tailwind utilities rather than a hex in the styles above — brand-500 is already
  // duplicated as a literal in wave-breadcrumb.component.ts and doesn't need a third copy.
  template: `
    <span class="ring rounded-full border-2 border-brand-500"></span>
    <span class="ring rounded-full border-2 border-brand-500"></span>
    <span class="ring rounded-full border-2 border-brand-500"></span>
    <span class="drop rounded-full bg-brand-500"></span>
  `
})
export class RippleLogoComponent {}
