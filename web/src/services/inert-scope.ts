/** A node may be reached by multiple background-isolation paths; retain its first prior state. */
export class InertScope<T extends {inert:boolean}> {
  private previous=new Map<T,boolean>();
  disable(element:T) { if(!this.previous.has(element))this.previous.set(element,element.inert); element.inert=true; }
  restore() { for(const [element,value] of this.previous)element.inert=value; this.previous.clear(); }
}
