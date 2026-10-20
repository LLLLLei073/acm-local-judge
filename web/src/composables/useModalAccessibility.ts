import { nextTick, onBeforeUnmount, watch, type Ref } from 'vue';
import { InertScope } from '../services/inert-scope';

/** Keep keyboard and accessibility focus inside the topmost modal, including stacked AI previews. */
export function useModalAccessibility(count: Ref<number>) {
  const scope=new InertScope<HTMLElement>();
  let revision=0,locked=false;
  let dialog: HTMLElement | undefined, returnFocus: HTMLElement | undefined, oldOverflow = '';
  const restoreInert = () => scope.restore();
  const tabbable = () => dialog ? [...dialog.querySelectorAll<HTMLElement>('button,input,textarea,select,a[href],[tabindex="0"]')].filter(el=>!el.matches(':disabled')&&!el.closest('[inert]')&&el.getClientRects().length>0) : [];
  const trap = (event: KeyboardEvent) => {
    if(event.key!=='Tab'||!dialog) return;
    const items=tabbable(),first=items[0],last=items.at(-1);
    if(!first) { event.preventDefault(); dialog.focus(); return; }
    if(event.shiftKey&&(document.activeElement===first||!dialog.contains(document.activeElement))) { event.preventDefault(); last!.focus(); }
    else if(!event.shiftKey&&(document.activeElement===last||!dialog.contains(document.activeElement))) { event.preventDefault(); first.focus(); }
  };
  watch(count, async value => {
    const version=++revision;
    await nextTick(); if(version!==revision)return; restoreInert();
    if(!value) { dialog=undefined; if(locked)document.body.style.overflow=oldOverflow; locked=false; if(returnFocus?.isConnected) returnFocus.focus(); returnFocus=undefined; return; }
    if(!locked) { returnFocus=document.activeElement as HTMLElement; oldOverflow=document.body.style.overflow; locked=true; }
    const modals=[...document.querySelectorAll<HTMLElement>('.modal-backdrop')];
    dialog=modals.at(-1)?.querySelector<HTMLElement>('[role="dialog"]')??undefined;
    if(!dialog) return;
    for(const element of document.querySelectorAll<HTMLElement>('.app-shell > *')) {
      if(element.contains(dialog)) continue;
      scope.disable(element);
    }
    for(const modal of modals) if(!modal.contains(dialog))scope.disable(modal);
    document.body.style.overflow='hidden';
    if(!dialog.contains(document.activeElement)) (dialog.querySelector<HTMLElement>('input:not([type="checkbox"]),textarea,select')??tabbable()[0])?.focus();
  },{flush:'post'});
  document.addEventListener('keydown',trap,true);
  onBeforeUnmount(()=>{revision++;restoreInert(); document.removeEventListener('keydown',trap,true); if(locked) document.body.style.overflow=oldOverflow;});
}
