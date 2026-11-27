/**
 * DOM HUD / 面板。只改既有元素的文本与 hidden 状态，不在 JS 里搭建布局。
 */
export class UI {
  constructor(doc, callbacks = {}) {
    this.doc = doc;
    this.callbacks = callbacks;
    this.rotateDismissed = false;
    this.el = {
      length: doc.getElementById('stat-length'),
      petal: doc.getElementById('stat-petal'),
      grow: doc.getElementById('stat-grow'),
      growBlock: doc.getElementById('stat-grow-block'),
      btnRestart: doc.getElementById('btn-restart'),
      btnPause: doc.getElementById('btn-pause'),
      btnSound: doc.getElementById('btn-sound'),
      btnFullscreen: doc.getElementById('btn-fullscreen'),
      failBanner: doc.getElementById('fail-banner'),
      winPanel: doc.getElementById('win-panel'),
      winSteps: doc.getElementById('win-steps'),
      btnReplay: doc.getElementById('btn-replay'),
      btnShare: doc.getElementById('btn-share'),
      pausePanel: doc.getElementById('pause-panel'),
      btnResume: doc.getElementById('btn-resume'),
      btnPauseRestart: doc.getElementById('btn-pause-restart'),
      btnPauseSound: doc.getElementById('btn-pause-sound'),
      rotateHint: doc.getElementById('rotate-hint'),
      btnRotateContinue: doc.getElementById('btn-rotate-continue'),
      loading: doc.getElementById('loading'),
    };
    this.bind();
    this.el.btnFullscreen.hidden = !doc.fullscreenEnabled;
    doc.addEventListener('fullscreenchange', () => {
      this.el.btnFullscreen.textContent = doc.fullscreenElement ? '退出全屏' : '全屏';
    });
    window.addEventListener('resize', () => this.maybeShowRotateHint());
    window.addEventListener('orientationchange', () => this.maybeShowRotateHint());
  }

  bind() {
    const click = (element, action) => {
      element.addEventListener('click', () => {
        this.callbacks.onButton?.();
        action();
      });
    };
    click(this.el.btnRestart, () => this.callbacks.onRestart?.());
    click(this.el.btnPause, () => this.callbacks.onTogglePause?.());
    click(this.el.btnSound, () => this.callbacks.onToggleMute?.());
    click(this.el.btnFullscreen, () => this.callbacks.onToggleFullscreen?.());
    click(this.el.btnReplay, () => this.callbacks.onRestart?.());
    click(this.el.btnShare, () => this.callbacks.onShare?.());
    click(this.el.btnResume, () => this.callbacks.onResume?.());
    click(this.el.btnPauseRestart, () => this.callbacks.onRestart?.());
    click(this.el.btnPauseSound, () => this.callbacks.onToggleMute?.());
    click(this.el.btnRotateContinue, () => {
      this.rotateDismissed = true;
      this.el.rotateHint.hidden = true;
    });
  }

  setStats({ length, maxLen, petalGain, growthCountdown, growthVisible }) {
    this.el.length.textContent = `${length}/${maxLen}`;
    this.el.petal.textContent = String(petalGain);
    this.el.grow.textContent = String(growthCountdown);
    this.el.growBlock.hidden = !growthVisible;
  }

  setMuted(muted) {
    const text = muted ? '音效 关' : '音效 开';
    this.el.btnSound.textContent = text;
    this.el.btnPauseSound.textContent = text;
  }

  setShareFeedback(text) {
    this.el.btnShare.textContent = text;
  }

  showFail(text) {
    this.el.failBanner.textContent = text;
    this.el.failBanner.hidden = false;
  }

  hideFail() {
    this.el.failBanner.hidden = true;
  }

  showWin(steps) {
    this.hideFail();
    this.el.winSteps.textContent = `步数 ${steps}`;
    this.el.winPanel.hidden = false;
    this.el.btnReplay.focus({ preventScroll: true });
  }

  hideWin() {
    this.el.winPanel.hidden = true;
    this.doc.activeElement?.blur?.();
  }

  showPause() {
    this.el.pausePanel.hidden = false;
    this.el.btnResume.focus({ preventScroll: true });
  }

  hidePause() {
    this.el.pausePanel.hidden = true;
    this.doc.activeElement?.blur?.();
  }

  showLoading() {
    this.el.loading.hidden = false;
  }

  hideLoading() {
    this.el.loading.hidden = true;
    this.maybeShowRotateHint();
  }

  maybeShowRotateHint() {
    if (this.rotateDismissed) {
      if (!this.isPortrait()) this.el.rotateHint.hidden = true;
      return;
    }
    const coarse = window.matchMedia?.('(pointer: coarse)').matches ?? false;
    if (coarse && this.isPortrait() && this.el.loading.hidden) {
      this.el.rotateHint.hidden = false;
    } else {
      this.el.rotateHint.hidden = true;
    }
  }

  isPortrait() {
    return window.innerHeight > window.innerWidth;
  }

  isWinOpen() {
    return !this.el.winPanel.hidden;
  }

  isPauseOpen() {
    return !this.el.pausePanel.hidden;
  }

  isRotateOpen() {
    return !this.el.rotateHint.hidden;
  }

  isModalOpen() {
    return this.isWinOpen() || this.isPauseOpen() || this.isRotateOpen();
  }
}
