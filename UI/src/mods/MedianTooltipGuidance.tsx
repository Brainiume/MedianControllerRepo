import { getModule, ModuleRegistry } from "cs2/modding";

const rendererPath = "game-ui/common/tooltip/tooltip-renderer/tooltip-renderer.tsx";
const guidanceType = "RoadGrass.Tooltip.ActionGuidance";
const stringType = "Game.UI.Tooltip.StringTooltip";
const hintType = "Game.UI.Tooltip.InputHintTooltip";

// Combine our adjacent text/hint widgets before the native group renders them.
// ControlIcons retains native mouse, keyboard, modifier and gamepad rendering.
export function registerMedianTooltipGuidance(registry: ModuleRegistry) {
    const classes = getModule(
        "game-ui/common/tooltip/tooltip-renderer/tooltip-renderer.module.scss", "classes"
    );
    const ControlIcons = getModule(
        "game-ui/common/input-events/action-hints/input-hint/control-icons.tsx", "ControlIcons"
    );
    const Localized = getModule("game-ui/common/localization/localized.tsx", "Localized");
    const FormattedParagraphs = getModule("game-ui/common/text/formatted-paragraphs.tsx", "FormattedParagraphs");
    const theme = { hint: classes.hint, label: classes.hintLabel, icon: classes.hintIcon };

    registry.extend(rendererPath, "tooltipComponents", (components: any) => ({
        ...components,
        [guidanceType]: ({ props }: any) => {
            const item = props.hint.items[0];
            const color = [undefined, classes.success, classes.warning, classes.error][props.color ?? 0];
            return <div className={classes.rowItem}>
                <ControlIcons bindings={item?.bindings ?? []} modifiers={item?.modifiers ?? []} theme={theme}>
                    <span className={`${classes.hintLabel}${color ? ` ${color}` : ""}`}>
                        <FormattedParagraphs className={classes.paragraphs}><Localized value={props.value} /></FormattedParagraphs>
                    </span>
                </ControlIcons>
            </div>;
        }
    }));

    registry.extend(rendererPath, "BoundTooltipGroup", (Component: any) => (props: any) => {
        const children: any[] = [];
        for (const widget of props.children) {
            const previous = children[children.length - 1];
            if (widget.props.__Type === hintType && widget.props.hint &&
                previous?.props.__Type === stringType &&
                String(previous.path).startsWith("RoadGrass.JoinMedian.")) {
                children[children.length - 1] = {
                    ...previous,
                    props: { ...previous.props, __Type: guidanceType, hint: widget.props.hint }
                };
            } else children.push(widget);
        }
        return <Component {...props} children={children} />;
    });
}
