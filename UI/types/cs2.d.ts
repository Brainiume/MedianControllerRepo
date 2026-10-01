declare module "cs2/modding" {
  export type ModuleRegistryExtend = (component: any) => (props?: any) => JSX.Element | boolean;

  export interface ModuleRegistry {
    registry: Map<string, Record<string, any>>;
    extend(path: string, exportName: string, extension: ModuleRegistryExtend): void;
  }

  export type ModRegistrar = (moduleRegistry: ModuleRegistry) => void;
  export function getModule(path: string, exportName: string): any;
}

declare module "cs2/api" {
  export interface ValueBinding<T> {
    value: T;
  }

  export function bindValue<T>(group: string, name: string): ValueBinding<T>;
  export function useValue<T>(binding: ValueBinding<T>): T;
  export function trigger(group: string, name: string, ...args: unknown[]): void;
}

declare module "cs2/l10n" {
  export function useLocalization(): {
    translate(key: string, fallback?: string): string;
  };
}

declare module "cs2/bindings" {
  export type Theme = Record<string, string>;
  export type UniqueFocusKey = unknown;
}
