package uk.co.adamkhattab.pcconnect.ui

import androidx.annotation.DrawableRes
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// ── text ─────────────────────────────────────────────────────────────────────

@Composable
fun ScreenTitle(text: String, modifier: Modifier = Modifier) {
    Text(text, modifier, color = PcColors.Ink, style = PcType.Screen)
}

/** "CONTROLS", "REMINDERS ON THIS PC" — the small capitals above a group. */
@Composable
fun SectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(text.uppercase(), modifier, color = PcColors.InkSoft, style = PcType.Section)
}

@Composable
fun Caption(text: String, modifier: Modifier = Modifier, color: Color = PcColors.InkFaint) {
    Text(text, modifier, color = color, style = PcType.Caption)
}

@Composable
fun FieldLabel(text: String, modifier: Modifier = Modifier) {
    Text(text, modifier, color = PcColors.InkSoft, style = PcType.Label)
}

// ── containers ───────────────────────────────────────────────────────────────

/** The white card everything sits in: one border colour, one radius. */
@Composable
fun PcCard(
    modifier: Modifier = Modifier,
    shape: RoundedCornerShape = PcShapes.Card,
    onClick: (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier
            .clip(shape)
            .background(PcColors.Surface)
            .border(1.dp, PcColors.Border, shape)
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
        content = content,
    )
}

/** A hairline between rows inside a card, indented past any leading column. */
@Composable
fun RowDivider(startIndent: Dp = 0.dp) {
    Box(
        Modifier
            .padding(start = startIndent)
            .fillMaxWidth()
            .height(1.dp)
            .background(PcColors.Divider),
    )
}

/** The tinted note the design uses to explain something without alarming. */
@Composable
fun InfoNote(
    text: String,
    modifier: Modifier = Modifier,
    @DrawableRes icon: Int = PcIcons.Info,
    background: Color = PcColors.PrimaryTint,
    iconTint: Color = PcColors.Primary,
) {
    Row(
        modifier
            .clip(PcShapes.Control)
            .background(background)
            .padding(14.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        PcIcon(icon, null, size = 22.dp, tint = iconTint)
        Text(text, color = PcColors.Ink, style = PcType.Caption.copy(fontSize = 13.sp, lineHeight = 19.5.sp))
    }
}

// ── status ───────────────────────────────────────────────────────────────────

/**
 * "Connected" or "Reconnecting", with the dot the design puts in front of it.
 *
 * A person needs to know whether the command they are about to send arrives now
 * or when the phone next has a socket, so this is a word and not only a colour.
 */
@Composable
fun ConnectionPill(connected: Boolean, modifier: Modifier = Modifier) {
    StatusPill(
        text = if (connected) "Connected" else "Reconnecting",
        ink = if (connected) PcColors.OnlineInk else PcColors.WarnInk,
        background = if (connected) PcColors.OnlineBg else PcColors.WarnBg,
        modifier = modifier,
        dot = if (connected) PcColors.OnlineDot else PcColors.WarnDot,
    )
}

@Composable
fun StatusPill(
    text: String,
    ink: Color,
    background: Color,
    modifier: Modifier = Modifier,
    dot: Color? = null,
) {
    Row(
        modifier
            .height(28.dp)
            .clip(PcShapes.Pill)
            .background(background)
            .padding(start = if (dot == null) 10.dp else 8.dp, end = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        if (dot != null) Dot(dot)
        Text(text, color = ink, style = PcType.Label, maxLines = 1)
    }
}

enum class Tone { Good, Bad, Neutral }

/** The outcome badge on a log or command row. */
@Composable
fun OutcomeBadge(text: String, tone: Tone, modifier: Modifier = Modifier) {
    val ink = when (tone) {
        Tone.Good -> PcColors.OnlineInk
        Tone.Bad -> PcColors.DangerInk
        Tone.Neutral -> PcColors.InkSoft
    }
    val background = when (tone) {
        Tone.Good -> PcColors.OnlineBg
        Tone.Bad -> PcColors.DangerBg
        Tone.Neutral -> PcColors.Track
    }

    Text(
        text,
        modifier
            .clip(PcShapes.Pill)
            .background(background)
            .padding(horizontal = 8.dp, vertical = 3.dp),
        color = ink,
        style = PcType.Label,
        maxLines = 1,
    )
}

@Composable
fun Dot(color: Color, size: Dp = 7.dp, modifier: Modifier = Modifier) {
    Box(modifier.size(size).clip(CircleShape).background(color))
}

// ── buttons ──────────────────────────────────────────────────────────────────

@Composable
fun PrimaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    height: Dp = 50.dp,
    container: Color = PcColors.Primary,
) {
    Box(
        modifier
            .fillMaxWidth()
            .height(height)
            .clip(PcShapes.Control)
            .background(if (enabled) container else PcColors.InkDisabled)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, color = Color.White, style = PcType.Button)
    }
}

/** White, bordered — the design's second-rank action. */
@Composable
fun QuietButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    @DrawableRes icon: Int? = null,
    enabled: Boolean = true,
    height: Dp = 46.dp,
    contentColour: Color = PcColors.Ink,
    iconTint: Color = PcColors.InkSoft,
) {
    Row(
        modifier
            .fillMaxWidth()
            .height(height)
            .clip(PcShapes.Control)
            .background(PcColors.Surface)
            .border(1.dp, PcColors.Border, PcShapes.Control)
            .clickable(enabled = enabled, onClick = onClick)
            .alpha(if (enabled) 1f else 0.5f),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
    ) {
        if (icon != null) {
            PcIcon(icon, null, size = 20.dp, tint = iconTint)
            Spacer(Modifier.width(8.dp))
        }
        Text(text, color = contentColour, style = PcType.BodySmall.copy(fontWeight = FontWeight.Medium))
    }
}

/** A borderless, coloured label that behaves like a link. */
@Composable
fun TextLink(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    colour: Color = PcColors.Primary,
    style: TextStyle = PcType.Label,
) {
    Text(text, modifier.clickable(onClick = onClick), color = colour, style = style)
}

// ── chips ────────────────────────────────────────────────────────────────────

enum class ChipStyle { Outline, Tinted, Selected, Disabled }

/**
 * The pill-shaped action under a PC, and the repeat options in the reminder
 * sheet. One shape, four tones.
 */
@Composable
fun PcChip(
    text: String,
    modifier: Modifier = Modifier,
    @DrawableRes icon: Int? = null,
    style: ChipStyle = ChipStyle.Outline,
    onClick: (() -> Unit)? = null,
) {
    val background = when (style) {
        ChipStyle.Tinted -> PcColors.PrimaryTint
        ChipStyle.Selected -> PcColors.Ink
        else -> Color.Transparent
    }
    val ink = when (style) {
        ChipStyle.Tinted -> PcColors.Primary
        ChipStyle.Selected -> Color.White
        ChipStyle.Disabled -> PcColors.InkDisabled
        ChipStyle.Outline -> PcColors.Ink
    }
    val iconTint = when (style) {
        ChipStyle.Tinted -> PcColors.Primary
        ChipStyle.Selected -> Color.White
        ChipStyle.Disabled -> PcColors.InkDisabled
        ChipStyle.Outline -> PcColors.InkSoft
    }
    val bordered = style == ChipStyle.Outline || style == ChipStyle.Disabled

    Row(
        modifier
            .height(34.dp)
            .clip(PcShapes.Pill)
            .background(background)
            .then(if (bordered) Modifier.border(1.dp, PcColors.Border, PcShapes.Pill) else Modifier)
            .then(
                if (onClick != null && style != ChipStyle.Disabled) {
                    Modifier.clickable(onClick = onClick)
                } else {
                    Modifier
                },
            )
            .padding(start = if (icon == null) 14.dp else 10.dp, end = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        if (icon != null) PcIcon(icon, null, size = 18.dp, tint = iconTint)
        Text(text, color = ink, style = PcType.Chip, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

// ── inputs ───────────────────────────────────────────────────────────────────

/**
 * The design's text field: a bordered box of a fixed height with the label
 * above it, rather than Material's floating label inside it.
 */
@Composable
fun PcTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    label: String? = null,
    placeholder: String? = null,
    height: Dp = 52.dp,
    singleLine: Boolean = true,
    enabled: Boolean = true,
    isError: Boolean = false,
    textStyle: TextStyle = PcType.Body.copy(fontSize = 15.5.sp),
    keyboardOptions: KeyboardOptions = KeyboardOptions.Default,
    visualTransformation: VisualTransformation = VisualTransformation.None,
    trailing: @Composable (RowScope.() -> Unit)? = null,
    labelTrailing: @Composable (() -> Unit)? = null,
) {
    Column(modifier) {
        if (label != null || labelTrailing != null) {
            Row(
                Modifier.fillMaxWidth().padding(bottom = 6.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.Bottom,
            ) {
                FieldLabel(label.orEmpty())
                labelTrailing?.invoke()
            }
        }

        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            enabled = enabled,
            singleLine = singleLine,
            textStyle = textStyle.copy(color = PcColors.Ink),
            cursorBrush = SolidColor(PcColors.Primary),
            keyboardOptions = keyboardOptions,
            visualTransformation = visualTransformation,
            modifier = Modifier.fillMaxWidth(),
            decorationBox = { inner ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .then(if (singleLine) Modifier.height(height) else Modifier.heightIn(min = height))
                        .clip(PcShapes.Control)
                        .background(PcColors.Surface)
                        .border(1.dp, if (isError) PcColors.Danger else PcColors.Border, PcShapes.Control)
                        .padding(horizontal = 14.dp, vertical = if (singleLine) 0.dp else 12.dp),
                    verticalAlignment = if (singleLine) Alignment.CenterVertically else Alignment.Top,
                ) {
                    Box(Modifier.weight(1f)) {
                        if (value.isEmpty() && placeholder != null) {
                            Text(placeholder, color = PcColors.InkFaint, style = textStyle)
                        }
                        inner()
                    }
                    trailing?.invoke(this)
                }
            },
        )
    }
}

/** A field that opens a picker rather than a keyboard. */
@Composable
fun PickerField(
    value: String,
    @DrawableRes icon: Int,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    label: String? = null,
    mono: Boolean = false,
) {
    Column(modifier) {
        if (label != null) {
            FieldLabel(label)
            Spacer(Modifier.height(6.dp))
        }

        Row(
            Modifier
                .fillMaxWidth()
                .height(48.dp)
                .clip(PcShapes.Control)
                .background(PcColors.Surface)
                .border(1.dp, PcColors.Border, PcShapes.Control)
                .clickable(onClick = onClick)
                .padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(
                value,
                color = PcColors.Ink,
                style = if (mono) PcType.MonoTime.copy(fontSize = 15.sp) else PcType.Body,
                maxLines = 1,
            )
            PcIcon(icon, null, size = 20.dp, tint = PcColors.InkFaint)
        }
    }
}

/** The design's switch: a 48×28 track with a 22 knob, not Material's. */
@Composable
fun PcSwitch(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val interaction = remember { MutableInteractionSource() }
    val track by animateColorAsState(
        if (checked) PcColors.Primary else PcColors.InkDisabled,
        label = "switchTrack",
    )
    val offset by animateDpAsState(if (checked) 23.dp else 3.dp, label = "switchKnob")

    Box(
        modifier
            .size(width = 48.dp, height = 28.dp)
            .clip(PcShapes.Pill)
            .background(track)
            .alpha(if (enabled) 1f else 0.55f)
            .clickable(
                interactionSource = interaction,
                indication = null,
                enabled = enabled,
            ) { onCheckedChange(!checked) },
    ) {
        Box(
            Modifier
                .padding(start = offset, top = 3.dp)
                .size(22.dp)
                .clip(CircleShape)
                .background(Color.White),
        )
    }
}

/** A round tick (a reminder) or a square one (choosing PCs). */
@Composable
fun PcCheck(
    checked: Boolean,
    modifier: Modifier = Modifier,
    round: Boolean = false,
    size: Dp = 22.dp,
    onClick: (() -> Unit)? = null,
) {
    val shape = if (round) CircleShape else RoundedCornerShape(6.dp)

    Box(
        modifier
            .size(size)
            .clip(shape)
            .then(
                if (checked) {
                    Modifier.background(if (round) PcColors.OnlineDot else PcColors.Primary)
                } else {
                    Modifier.border(1.5.dp, PcColors.InkDisabled, shape)
                },
            )
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
        contentAlignment = Alignment.Center,
    ) {
        if (checked) PcIcon(PcIcons.Check, null, size = size * 0.7f, tint = Color.White)
    }
}

/** The two-up segmented control the reminder sheet uses for its scope. */
@Composable
fun SegmentedPair(
    options: List<String>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier
            .fillMaxWidth()
            .clip(PcShapes.Control)
            .background(PcColors.Track)
            .padding(4.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        options.forEachIndexed { index, label ->
            val selected = index == selectedIndex

            Box(
                Modifier
                    .weight(1f)
                    .height(38.dp)
                    .clip(RoundedCornerShape(9.dp))
                    .background(if (selected) PcColors.Surface else Color.Transparent)
                    .clickable { onSelect(index) },
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    label,
                    color = if (selected) PcColors.Ink else PcColors.InkSoft,
                    style = PcType.BodySmall.copy(
                        fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
                    ),
                )
            }
        }
    }
}

// ── chrome ───────────────────────────────────────────────────────────────────

/** The 56dp bar at the top of a tab. */
@Composable
fun PcTopBar(
    title: String,
    modifier: Modifier = Modifier,
    trailing: @Composable (RowScope.() -> Unit)? = null,
) {
    Row(
        modifier
            .fillMaxWidth()
            .height(56.dp)
            .padding(start = 20.dp, end = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        ScreenTitle(title, Modifier.weight(1f))
        trailing?.invoke(this)
    }
}

/** A 44dp touch target around a 22dp glyph, which is what the design's bars use. */
@Composable
fun IconAction(
    @DrawableRes icon: Int,
    contentDescription: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    tint: Color = PcColors.InkSoft,
) {
    Box(
        modifier
            .size(44.dp)
            .clip(CircleShape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        PcIcon(icon, contentDescription, size = 22.dp, tint = tint)
    }
}
