import { createContext, useContext, type JSX } from "solid-js";
import type { FrontendWorkController } from "./useFrontendWorkController";

const FrontendWorkContext = createContext<FrontendWorkController>();

export function FrontendWorkProvider(props: {
  controller: FrontendWorkController;
  children: JSX.Element;
}) {
  return (
    <FrontendWorkContext.Provider value={props.controller}>
      {props.children}
    </FrontendWorkContext.Provider>
  );
}

export function useFrontendWork() {
  const controller = useContext(FrontendWorkContext);
  if (!controller) {
    throw new Error("Frontend work controller is not available.");
  }
  return controller;
}
