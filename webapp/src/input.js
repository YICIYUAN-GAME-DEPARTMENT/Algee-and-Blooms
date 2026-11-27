/**
 * 输入：键盘（方向键/WASD/R/Esc/P/M）、画布滑动、移动端虚拟方向键。
 * 一次手势/一次按下只走一步；生长锁/弹窗期间由 isBlocked 屏蔽方向输入。
 */

const KEY_DIRS = {
  ArrowUp: 'up',
  KeyW: 'up',
  ArrowDown: 'down',
  KeyS: 'down',
  ArrowLeft: 'left',
  KeyA: 'left',
  ArrowRight: 'right',
  KeyD: 'right',
};

const SWIPE_THRESHOLD = 26;

export function createInput({
  canvas,
  dpad,
  onStep,
  onRestart,
  onTogglePause,
  onToggleMute,
  onGesture,
  isBlocked,
}) {
  function gesture() {
    onGesture?.();
  }

  function handleKeyDown(event) {
    if (event.repeat) return;
    if (event.code === 'ArrowUp' || event.code === 'ArrowDown' || event.code === 'ArrowLeft' || event.code === 'ArrowRight') {
      event.preventDefault();
    }
    gesture();

    const dir = KEY_DIRS[event.code];
    if (dir) {
      if (!isBlocked()) onStep(dir);
      return;
    }
    if (event.code === 'KeyR') {
      onRestart();
      return;
    }
    if (event.code === 'Escape' || event.code === 'KeyP') {
      onTogglePause();
      return;
    }
    if (event.code === 'KeyM') {
      onToggleMute();
    }
  }

  let swipe = null;

  function handlePointerDown(event) {
    gesture();
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    if (!canvas.contains(event.target)) return;
    if (isBlocked()) return;
    swipe = { id: event.pointerId, x: event.clientX, y: event.clientY };
    try {
      canvas.setPointerCapture(event.pointerId);
    } catch {
      // 某些浏览器不支持 pointer capture，忽略即可
    }
  }

  function handlePointerUp(event) {
    if (!swipe || swipe.id !== event.pointerId) return;
    const dx = event.clientX - swipe.x;
    const dy = event.clientY - swipe.y;
    swipe = null;
    if (isBlocked()) return;
    if (Math.max(Math.abs(dx), Math.abs(dy)) < SWIPE_THRESHOLD) return;
    if (Math.abs(dx) >= Math.abs(dy)) {
      onStep(dx > 0 ? 'right' : 'left');
    } else {
      onStep(dy > 0 ? 'down' : 'up');
    }
  }

  function handlePointerCancel() {
    swipe = null;
  }

  function handleDpad(event) {
    const button = event.target.closest?.('[data-dir]');
    if (!button) return;
    event.preventDefault();
    gesture();
    if (isBlocked()) return;
    onStep(button.dataset.dir);
  }

  function handleBlur() {
    swipe = null;
  }

  window.addEventListener('keydown', handleKeyDown);
  canvas.addEventListener('pointerdown', handlePointerDown);
  canvas.addEventListener('pointerup', handlePointerUp);
  canvas.addEventListener('pointercancel', handlePointerCancel);
  dpad.addEventListener('pointerdown', handleDpad);
  window.addEventListener('blur', handleBlur);

  return {
    destroy() {
      window.removeEventListener('keydown', handleKeyDown);
      canvas.removeEventListener('pointerdown', handlePointerDown);
      canvas.removeEventListener('pointerup', handlePointerUp);
      canvas.removeEventListener('pointercancel', handlePointerCancel);
      dpad.removeEventListener('pointerdown', handleDpad);
      window.removeEventListener('blur', handleBlur);
    },
  };
}
