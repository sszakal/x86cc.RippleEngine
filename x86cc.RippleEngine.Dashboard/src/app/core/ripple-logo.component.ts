import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The brand mark: a drop with rings spreading out of it.
 *
 * The motion is a status indicator, not decoration — the rings only ripple while `active` is set (the
 * cluster is executing), and settle into a static concentric-target glyph when it isn't. That static glyph
 * is also the `prefers-reduced-motion` fallback, so it has to read as a finished mark on its own.
 *
 * Presentational by design: it takes a boolean and draws. Whoever mounts it decides what "active" means
 * (the sidebar wires it to `EngineActivityService`), so the mark stays reusable as a loading glyph.
 */
@Component({
  selector: 'app-ripple-logo',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    role: 'img',
    '[class.is-active]': 'active()',
    // The colour is the only thing the animation says out loud; give the state a text equivalent. Deliberately
    // NOT the word "Ripple" — the wordmark beside it already carries the name, and this would double it up.
    '[attr.aria-label]': 'active() ? "Engine active" : "Engine idle"',
    '[attr.title]': 'active() ? "Engine active" : "Engine idle"'
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

      .ring {
        position: absolute;
        inset: 0;
        transform-origin: center;
        will-change: transform, opacity;
        transition: transform 0.5s ease, opacity 0.5s ease;
      }

      /*
       * Idle: the rings hold their positions as a static target. Each is a step further out and a step
       * fainter, so the mark still reads as "spreading" with nothing moving.
       */
      .ring:nth-child(1) { transform: scale(0.45); opacity: 0.5; }
      .ring:nth-child(2) { transform: scale(0.72); opacity: 0.28; }
      .ring:nth-child(3) { transform: scale(1);    opacity: 0.14; }

      /*
       * Active: every ring runs the same expand-and-fade, offset by a third of the cycle each — the CSS
       * equivalent of the motion.dev example's stagger(). Only transform and opacity animate, so this stays
       * on the compositor and costs nothing while it loops.
       */
      :host(.is-active) .ring {
        animation: ripple-out 2.4s cubic-bezier(0, 0, 0.2, 1) infinite;
        transition: none; /* the idle ease would fight the keyframes on the first frame of the handoff */
      }
      :host(.is-active) .ring:nth-child(2) { animation-delay: 0.8s; }
      :host(.is-active) .ring:nth-child(3) { animation-delay: 1.6s; }

      @keyframes ripple-out {
        0%   { transform: scale(0.28); opacity: 0.85; }
        70%  { opacity: 0.22; }
        100% { transform: scale(1);    opacity: 0; }
      }

      /* Drop it back to the idle glyph: with the animation off, the base .ring rules apply again. */
      @media (prefers-reduced-motion: reduce) {
        :host(.is-active) .ring { animation: none; }
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
export class RippleLogoComponent {
  /** True while the cluster is executing ripples; drives the loop on/off. */
  readonly active = input(false);
}
